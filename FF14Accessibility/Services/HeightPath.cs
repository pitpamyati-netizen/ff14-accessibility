using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace FF14Accessibility.Services;

/// <summary>
/// Query-only planning for every ground destination. A duplicated endpoint
/// is not proof of arrival: vnavmesh can return it even for a disconnected mesh.
/// Every leg must end on the requested floor and have mesh support along it.
/// Update runs on the framework thread with a bounded number of probes per tick.
/// </summary>
internal sealed class HeightPath : IDisposable
{
    private readonly Func<Vector3, float, float, Vector3?> _nearest;
    private readonly Func<Vector3, Vector3, CancellationToken, Task<List<Vector3>>?> _query;
    private readonly Func<Vector3, Vector3, bool>? _segmentClear;
    private readonly CancellationTokenSource _cancel = new();
    private readonly float _range;
    private readonly List<Vector3> _candidates = new();
    private readonly List<Vector3> _starts = new();
    private Task<List<Vector3>>? _pending;
    private List<Vector3>? _endpointPath;
    private IEnumerator<Vector3>? _samples;
    private List<Vector3>? _leg;
    private List<Vector3>? _firstLeg;
    private Vector3 _goal, _via;
    private Vector3 _surface;
    private int _candidate, _startCandidate;
    private bool _initialized, _direct = true, _disposed;
    private readonly bool _directOnly;

    internal Vector3 Start { get; }
    internal Vector3 Destination { get; }
    internal bool Done { get; private set; }
    internal List<Vector3>? Result { get; private set; }
    internal int Queries { get; private set; }
    internal string LastFailure { get; private set; } = "none";
    internal Vector3? NativePartialEnd { get; private set; }

    internal HeightPath(Vector3 start, Vector3 destination, float range,
        Func<Vector3, float, float, Vector3?> nearest,
        Func<Vector3, Vector3, CancellationToken, Task<List<Vector3>>?> query,
        Func<Vector3, Vector3, bool>? segmentClear = null, float? approachRange = null,
        bool directOnly = false)
    {
        Start = start; Destination = destination; _range = approachRange ?? range;
        _nearest = nearest; _query = query;
        _segmentClear = segmentClear;
        _directOnly = directOnly;
    }

    internal void Update()
    {
        if (Done) return;
        if (!_initialized)
        {
            _initialized = true;
            if (!Finite(Start) || !Finite(Destination) || !float.IsFinite(_range) || _range <= 0)
            { LastFailure = "invalid destination"; Done = true; return; }
            // Narrow Y is essential: never snap a known NPC height to the floor below.
            var goal = _nearest(Destination, _range, 1);
            if (goal is not { } point || !Finite(point) || MathF.Abs(point.Y - Destination.Y) > 1
                || Vector3.Distance(point, Destination) > _range)
            { LastFailure = "no mesh point within target height and interaction range"; Done = true; return; }
            _goal = point;
            foreach (var radius in new[] { 2f, 4f, 8f })
                for (var i = 0; i < 8; ++i)
                    _starts.Add(Start + new Vector3(MathF.Cos(i * MathF.PI / 4) * radius, 0,
                        MathF.Sin(i * MathF.PI / 4) * radius));
            foreach (var radius in new[] { 4f, 8f, 16f })
                for (var i = 0; i < 8; ++i)
                    _candidates.Add(_goal + new Vector3(MathF.Cos(i * MathF.PI / 4) * radius, 0,
                        MathF.Sin(i * MathF.PI / 4) * radius));
            Query(Start, _goal);
            return;
        }
        if (_pending != null)
        {
            if (!_pending.IsCompleted) return;
            var task = _pending;
            _pending = null;
            if (task.IsFaulted || task.IsCanceled)
            { _ = task.Exception; LastFailure = "query failed or canceled"; Reject(); return; }
            var from = _firstLeg == null ? Start : _via;
            var to = _direct || _firstLeg != null ? _goal : _via;
            var path = task.Result;
            if (_endpointPath == null && path.Count >= 3 && Finite(path[^2])
                && !HasNativeEndpoint(path, to) && Vector3.Distance(from, path[^2]) > 1)
                NativePartialEnd = path[^2];
            var endpointVerified = _endpointPath != null;
            if (_endpointPath != null)
            {
                // Polygon-centre routes may end far from the requested point.
                // Ask from the goal back to the actual last polygon: a partial
                // route from the other island cannot certify that connection.
                var endpoint = _endpointPath[^2];
                if (!ValidShape(path, to, endpoint) || !HasNativeEndpoint(path, endpoint))
                { LastFailure = "partial native endpoint"; Reject(); return; }
                path = _endpointPath;
                _endpointPath = null;
            }
            if (path.Count < 2 || path.Count > 1024 || !ValidShape(path, from, to))
            { LastFailure = "partial path or unsupported height change"; Reject(); return; }
            if (!endpointVerified && !HasNativeEndpoint(path, to))
            {
                _endpointPath = new List<Vector3>(path);
                Query(to, path[^2]);
                return;
            }
            // Own the list; vnavmesh must not be able to prune the data we check.
            _samples = Samples(new List<Vector3>(path), from).GetEnumerator();
            _leg = new List<Vector3>();
            _surface = from;
        }
        if (_samples != null)
        {
            for (var i = 0; i < 24; ++i)
            {
                if (!_samples.MoveNext()) { AcceptLeg(); return; }
                var sample = _samples.Current;
                // A string-pulled ground route gives horizontal corners, not
                // every change in terrain height. Trace the actual floor from
                // the previous supported point instead of demanding that the
                // floor coincide with a straight 3D line between the corners.
                var step = GroundDetour.FlatDistance(_surface, sample);
                var maxRise = MathF.Max(0.75f, step * 1.5f);
                var probe = sample with { Y = _surface.Y };
                // Recast erodes edges and quantizes stairs. Small seams are
                // allowed, while each sample must still stay on nearby ground.
                var floor = _nearest(probe, 1.5f, maxRise);
                if (floor is not { } p || !Finite(p) || MathF.Abs(p.Y - _surface.Y) > maxRise
                    || GroundDetour.FlatDistance(p, sample) > 1.5f)
                { LastFailure = $"no surface at ({sample.X:F2}|{sample.Y:F2}|{sample.Z:F2})"; Reject(); return; }
                if (_segmentClear != null && !_segmentClear(_surface, p))
                { LastFailure = $"collision across surface connector: {_surface} -> {p}"; Reject(); return; }
                _surface = p;
                // Walk the supported surface, not the sparse straight line
                // that may run beside a stairway or across an eroded edge.
                if (_leg!.Count == 0 || Vector3.Distance(_leg[^1], p) > 0.05f) _leg.Add(p);
            }
            return;
        }
        // A candidate is only a place to ASK vnavmesh about, never a direct move.
        if (_candidate < _candidates.Count)
        {
            var probe = _candidates[_candidate++];
            var via = _nearest(probe, 1, 2);
            if (via is not { } p || !Finite(p) || MathF.Abs(p.Y - _goal.Y) > 2
                || GroundDetour.FlatDistance(probe, p) > 1) return;
            _via = p;
            Query(Start, _via);
            return;
        }
        if (_startCandidate < _starts.Count)
        {
            // An isolated start polygon cannot discover the surrounding ramp.
            // A query from a nearby origin still traces the WHOLE connector
            // from Start; no movement occurs unless all its steps are supported.
            var probe = _starts[_startCandidate++];
            var origin = _nearest(probe, 1, 2);
            if (origin is not { } p || !Finite(p) || MathF.Abs(p.Y - Start.Y) > 2
                || GroundDetour.FlatDistance(probe, p) > 1) return;
            _direct = true;
            Query(p, _goal);
            return;
        }
        Done = true;
    }

    private void Query(Vector3 from, Vector3 to)
    {
        ++Queries;
        _pending = _query(from, to, _cancel.Token);
        // Missing IPC is not a reason to keep asking or to start a blind walk.
        if (_pending == null) { LastFailure = "path query IPC unavailable"; Done = true; }
    }

    private void Reject()
    {
        _samples?.Dispose(); _samples = null;
        _leg = null; _firstLeg = null; _endpointPath = null; _direct = false;
        if (_directOnly) Done = true;
    }

    private void AcceptLeg()
    {
        _samples!.Dispose(); _samples = null;
        var endpoint = _direct || _firstLeg != null ? _goal : _via;
        if (Vector3.Distance(_surface, endpoint) > 0.75f)
        { LastFailure = "surface trace ends on another floor"; Reject(); return; }
        if (!ValidShape(_leg, _firstLeg == null ? Start : _via, endpoint))
        { LastFailure = "surface trace contains an unsupported step"; Reject(); return; }
        var leg = _leg!; _leg = null;
        if (!_direct && _firstLeg == null)
        {
            _firstLeg = leg;
            Query(_via, _goal);
            return;
        }
        var combined = _firstLeg == null ? leg : new List<Vector3>(_firstLeg);
        if (_firstLeg != null)
            foreach (var p in leg)
                if (Vector3.Distance(combined[^1], p) > 0.05f) combined.Add(p);
        Result = combined;
        LastFailure = "none";
        Done = true;
    }

    internal static bool ValidShape(IReadOnlyList<Vector3>? path, Vector3 from, Vector3 to)
    {
        if (path == null || path.Count == 0 || path.Count > 8192 || !Finite(from) || !Finite(to)) return false;
        // Polygon-center routes need not begin at the exact start, and a valid
        // route need not duplicate its endpoint. Surface tracing validates
        // those connections and still rejects an appended unreachable goal.
        if (Vector3.Distance(path[^1], to) > 0.75f) return false;
        var previous = from;
        var length = 0f;
        foreach (var p in path)
        {
            if (!Finite(p)) return false;
            var rise = MathF.Abs(p.Y - previous.Y);
            // Also catches the logged [start, target, target] false success.
            if (rise > 1 && rise > GroundDetour.FlatDistance(previous, p) * 1.45f) return false;
            length += Vector3.Distance(previous, p);
            if (length > 8000) return false;
            previous = p;
        }
        return true;
    }

    private static IEnumerable<Vector3> Samples(IReadOnlyList<Vector3> path, Vector3 from)
    {
        var previous = from;
        foreach (var p in path)
        {
            var count = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(previous, p)));
            for (var i = 1; i <= count; ++i) yield return Vector3.Lerp(previous, p, (float)i / count);
            previous = p;
        }
    }

    internal static bool IsRemainingPath(IReadOnlyList<Vector3> planned, IReadOnlyList<Vector3> remaining)
    {
        if (remaining.Count > planned.Count) return false;
        var offset = planned.Count - remaining.Count;
        for (var i = 0; i < remaining.Count; ++i)
            if (!Finite(remaining[i]) || Vector3.Distance(planned[offset + i], remaining[i]) > 0.05f) return false;
        return true;
    }

    internal static bool HasNativeEndpoint(IReadOnlyList<Vector3> path, Vector3 goal)
        => path.Count >= 2 && Finite(path[^2]) && Vector3.Distance(path[^2], goal) <= 0.75f;

    internal static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Done = true; Result = null;
        _samples?.Dispose(); _samples = null;
        _cancel.Cancel();
        if (_pending is { } pending)
            _ = pending.ContinueWith(t => { _ = t.Exception; _cancel.Dispose(); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        else _cancel.Dispose();
        _pending = null;
    }
}
