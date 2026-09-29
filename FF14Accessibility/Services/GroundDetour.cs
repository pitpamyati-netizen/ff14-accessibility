using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace FF14Accessibility.Services;

/// <summary>
/// A short, mesh-derived walking route around a corner where the character has
/// already stalled. Queries only: the framework thread decides whether to move.
/// Each leg must reach its endpoint before vnavmesh's appended destination and
/// stay away from the failed corner. Cancellation never starts a movement.
/// </summary>
internal sealed class GroundDetour : IDisposable
{
    internal const float CornerRadius = 0.4f;
    private readonly Func<Vector3, Vector3?> _nearest;
    private readonly Func<Vector3, Vector3, CancellationToken, Task<List<Vector3>>?> _query;
    private readonly CancellationTokenSource _cancel = new();
    private readonly List<Vector3> _candidates;
    private Task<List<Vector3>>? _pending;
    private List<Vector3>? _firstLeg;
    private Vector3 _via;
    private int _index;
    private bool _disposed;

    internal Vector3 Start { get; }
    internal Vector3 Corner { get; }
    internal Vector3 End { get; }
    internal bool Done { get; private set; }
    internal List<Vector3>? Result { get; private set; }

    internal GroundDetour(Vector3 start, Vector3 corner, Vector3 end,
        Func<Vector3, Vector3?> nearest,
        Func<Vector3, Vector3, CancellationToken, Task<List<Vector3>>?> query)
    {
        Start = start; Corner = corner; End = end;
        _nearest = nearest; _query = query;
        _candidates = Candidates(start, corner);
    }

    internal static bool TryChoose(Vector3 position, IReadOnlyList<Vector3> path,
        out Vector3 corner, out Vector3 end)
    {
        corner = end = default;
        // The final point is merely the requested destination; never use it as
        // proof of a connected walking surface on the other side of an obstacle.
        if (!Finite(position) || path.Count < 3 || !Finite(path[0])) return false;
        corner = path[0];
        var flat = FlatDistance(position, corner);
        var rise = corner.Y - position.Y;
        if (flat < CornerRadius + 0.05f || flat > 1.5f || rise < 0.3f || rise > 1.2f)
            return false;
        for (var i = 1; i < path.Count - 1; ++i)
        {
            var p = path[i];
            if (!Finite(p)) return false;
            var distance = FlatDistance(position, p);
            if (distance > 6f) break;
            if (distance >= 2f && FlatDistance(corner, p) >= 1f && MathF.Abs(p.Y - position.Y) <= 1f)
            {
                end = p;
                return true;
            }
        }
        return false;
    }

    /// <summary>Call only from the framework thread: one query transition per tick.</summary>
    internal void Update()
    {
        if (Done) return;
        if (_pending != null)
        {
            if (!_pending.IsCompleted) return;
            var finished = _pending;
            _pending = null;
            if (finished.IsFaulted || finished.IsCanceled)
            {
                _ = finished.Exception; // observe faults; the next candidate may still work
                _firstLeg = null;
                return;
            }
            var leg = finished.Result;
            var legStart = _firstLeg == null ? Start : _via;
            var legEnd = _firstLeg == null ? _via : End;
            if (!ValidLeg(leg, legStart, legEnd, Corner))
            {
                _firstLeg = null;
                return;
            }
            if (_firstLeg == null)
            {
                _firstLeg = leg;
                _pending = _query(_via, End, _cancel.Token);
                if (_pending == null) _firstLeg = null;
                return;
            }
            var combined = new List<Vector3>(_firstLeg);
            foreach (var point in leg)
                if (Vector3.Distance(combined[^1], point) > 0.05f) combined.Add(point);
            if (Length(combined) <= 16f)
            {
                Result = combined;
                Done = true;
            }
            _firstLeg = null;
            return;
        }

        while (_index < _candidates.Count)
        {
            var probe = _candidates[_index++];
            var nearest = _nearest(probe);
            if (nearest is not { } via || !Finite(via)
                || FlatDistance(probe, via) > 0.5f || MathF.Abs(via.Y - Start.Y) > 0.5f
                || FlatDistance(via, Start) < 0.6f || SegmentNear(Start, via, Corner)) continue;
            _via = via;
            _pending = _query(Start, via, _cancel.Token);
            if (_pending != null) return;
        }
        Done = true;
    }

    internal static bool ValidLeg(IReadOnlyList<Vector3>? path, Vector3 from, Vector3 to, Vector3 corner)
    {
        if (path == null || path.Count < 2 || path.Count > 32
            || !Finite(from) || !Finite(to) || !Finite(corner)) return false;
        // PathfindMesh always appends 'to', even for a partial path. Checking
        // that point alone would recreate the very wall-pushing we are fixing.
        if (Vector3.Distance(path[^2], to) > 0.35f || Vector3.Distance(path[^1], to) > 0.35f
            || Vector3.Distance(path[0], from) > 0.5f) return false;
        var previous = from;
        var length = 0f;
        foreach (var p in path)
        {
            if (!Finite(p) || SegmentNear(previous, p, corner)) return false;
            var flat = FlatDistance(previous, p);
            var rise = MathF.Abs(p.Y - previous.Y);
            // No vertical jumps or drops disguised as a walking detour.
            if (rise > 1f || (rise > 0.25f && rise > flat * 0.65f)) return false;
            length += Vector3.Distance(previous, p);
            previous = p;
        }
        return length <= 12f;
    }

    private static List<Vector3> Candidates(Vector3 start, Vector3 corner)
    {
        var forward = corner - start;
        forward.Y = 0;
        forward = Vector3.Normalize(forward);
        var side = new Vector3(forward.Z, 0, -forward.X);
        var result = new List<Vector3>();
        foreach (var radius in new[] { 1f, 2f, 3f })
            foreach (var direction in new[] { side, -side, Vector3.Normalize(side - forward),
                         Vector3.Normalize(-side - forward) })
                result.Add(start + direction * radius);
        return result;
    }

    internal static bool SegmentNear(Vector3 a, Vector3 b, Vector3 center)
    {
        var p = new Vector2(a.X, a.Z);
        var d = new Vector2(b.X - a.X, b.Z - a.Z);
        var c = new Vector2(center.X, center.Z);
        var t = d.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(c - p, d) / d.LengthSquared(), 0, 1) : 0;
        return Vector2.Distance(p + d * t, c) < CornerRadius;
    }

    internal static float FlatDistance(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static float Length(IReadOnlyList<Vector3> path)
    {
        var total = 0f;
        for (var i = 1; i < path.Count; ++i) total += Vector3.Distance(path[i - 1], path[i]);
        return total;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Done = true;
        Result = null;
        _cancel.Cancel();
        if (_pending is { } pending)
            _ = pending.ContinueWith(t => { _ = t.Exception; _cancel.Dispose(); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        else _cancel.Dispose();
        _pending = null;
    }
}
