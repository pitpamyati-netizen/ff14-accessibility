using System.Numerics;

namespace FF14Accessibility.Services;

/// <summary>Executes copied, typed route legs. A jump never completes by XZ
/// waypoint pruning: actual takeoff and landing on the destination floor count.</summary>
internal sealed class GroundRouteRunner
{
    private readonly List<GroundLeg> _legs = [];
    private readonly Func<List<Vector3>, bool> _move;
    private readonly Action _stop;
    private readonly Func<List<Vector3>> _remaining;
    private readonly Func<bool> _nativeSearching;
    private readonly Func<Vector3, Vector3, bool> _clear;
    private readonly Func<Vector3, Vector3, bool> _jumpClear;
    private readonly Func<Vector3, bool> _jump;
    private readonly Func<Vector3, bool> _landed;
    private int _index;
    private bool _started, _airborne;
    private bool _jumpCommandSent;
    private DateTime _startedAt, _lastMoveAt;
    private Vector3 _lastPosition;
    private List<Vector3>? _planned;
    private List<Vector3> _lastRemaining = [];
    private DateTime? _landingAt;
    private Vector3 _landingPosition;
    internal bool Done { get; private set; }
    internal bool Failed { get; private set; }
    internal string Reason { get; private set; } = "none";
    internal GroundFailure? Failure { get; private set; }
    internal bool IsJump => !Done && _index < _legs.Count && _legs[_index].Jump;
    internal bool JumpStarted => _jumpCommandSent;

    internal GroundRouteRunner(IEnumerable<GroundLeg> route, Func<List<Vector3>, bool> move, Action stop,
        Func<List<Vector3>> remaining, Func<bool> nativeSearching,
        Func<Vector3, Vector3, bool> clear, Func<Vector3, Vector3, bool> jumpClear, Func<Vector3, bool> jump,
        Func<Vector3, bool> landed)
    {
        foreach (var leg in route)
        {
            if (leg.Points.Count == 0 || !HeightPath.Finite(leg.From) || leg.Points.Any(p => !HeightPath.Finite(p)))
            { Done = Failed = true; Reason = "invalid route"; break; }
            if ((leg.Jump && leg.Points.Count != 1) || (_legs.Count > 0 && Vector3.Distance(_legs[^1].End, leg.From) > 0.5f))
            { Done = Failed = true; Reason = "disconnected route legs"; break; }
            if (!leg.Jump && _legs.Count > 0 && !_legs[^1].Jump)
                _legs[^1].Points.AddRange(leg.Points);
            else _legs.Add(new GroundLeg(leg.From, new List<Vector3>(leg.Points), leg.Jump));
        }
        _move = move; _stop = stop; _remaining = remaining; _nativeSearching = nativeSearching;
        _clear = clear; _jumpClear = jumpClear; _jump = jump;
        _landed = landed;
        if (_legs.Count == 0) { Done = Failed = true; Reason = "empty route"; }
    }

    internal void Update(Vector3 position, bool jumping, DateTime now)
    {
        if (Done) return;
        var leg = _legs[_index];
        if (!HeightPath.Finite(position)) { Fail(position, leg.End, "invalid player position"); return; }
        if (!_started)
        {
            if (Vector3.Distance(position, leg.From) > 0.75f)
            { Fail(position, leg.End, "route origin changed"); return; }
            if (leg.Jump)
            {
                if (jumping || Vector3.Distance(position, leg.From) > 0.6f || !_jumpClear(position, leg.End))
                { Fail(position, leg.End, "jump launch or landing changed"); return; }
                _stop();
                if (!_jump(leg.End)) { Fail(position, leg.End, "jump action rejected"); return; }
                _jumpCommandSent = true;
            }
            else if (!_clear(position, NextPoint(position, leg.Points)))
            { Fail(position, NextPoint(position, leg.Points), "walking edge blocked before start"); return; }
            _planned = new List<Vector3>(leg.Points);
            _lastRemaining = new List<Vector3>(_planned);
            if (!_move(new List<Vector3>(_planned))) { Fail(position, leg.End, "movement rejected"); return; }
            _started = true; _airborne = false;
            _startedAt = _lastMoveAt = now; _lastPosition = position;
            return;
        }
        var remaining = _remaining();
        if (_nativeSearching() || !HeightPath.IsRemainingPath(_planned!, remaining))
        { Fail(position, NextPoint(position, _lastRemaining), "native route replaced"); return; }
        if (remaining.Count > 0) _lastRemaining = new List<Vector3>(remaining);
        if (Vector3.Distance(position, _lastPosition) >= 0.25f)
        { _lastPosition = position; _lastMoveAt = now; }
        if (leg.Jump)
        {
            _airborne |= jumping;
            if (_airborne && !jumping && GroundDetour.FlatDistance(position, leg.End) <= 0.5f
                && MathF.Abs(position.Y - leg.End.Y) <= 0.35f && _landed(position))
            {
                if (_landingAt == null || MathF.Abs(position.Y - _landingPosition.Y) > 0.05f)
                { _landingAt = now; _landingPosition = position; }
                else if (now - _landingAt.Value >= TimeSpan.FromSeconds(0.1)) { Advance(); return; }
            }
            else _landingAt = null;
            if ((now - _startedAt).TotalSeconds > 2.5
                || (!_airborne && (now - _startedAt).TotalSeconds > 0.8))
            { Fail(leg.From, leg.End, "jump did not land on the requested floor"); return; }
        }
        else
        {
            var tolerance = _index + 1 < _legs.Count && _legs[_index + 1].Jump ? 0.25f : 0.45f;
            if (Vector3.Distance(position, leg.End) <= tolerance) { Advance(); return; }
            if (remaining.Count > 0 && !_clear(position, NextPoint(position, remaining)))
            { Fail(position, NextPoint(position, remaining), "walking edge blocked"); return; }
            if ((now - _lastMoveAt).TotalSeconds > 2 || remaining.Count == 0)
            { Fail(position, NextPoint(position, _lastRemaining), "walking leg stalled or ended early"); return; }
        }
    }

    private static Vector3 NextPoint(Vector3 position, IReadOnlyList<Vector3> points)
    {
        var point = points.FirstOrDefault(p => Vector3.Distance(p, position) > 0.4f, points[^1]);
        var delta = point - position;
        return delta.Length() > 1.5f ? position + Vector3.Normalize(delta) * 1.5f : point;
    }

    private void Advance()
    {
        _stop(); ++_index; _started = false; _airborne = false;
        _jumpCommandSent = false;
        _landingAt = null;
        if (_index == _legs.Count) Done = true;
    }
    private void Fail(Vector3 from, Vector3 to, string reason)
    {
        if (_legs[_index].Jump) { from = _legs[_index].From; to = _legs[_index].End; }
        Failure = new GroundFailure(from, to, _legs[_index].Jump);
        _stop(); Done = Failed = true; Reason = reason;
    }
}
