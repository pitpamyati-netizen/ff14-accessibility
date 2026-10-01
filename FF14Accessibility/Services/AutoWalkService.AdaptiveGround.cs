using System.Numerics;

namespace FF14Accessibility.Services;

public sealed partial class AutoWalkService
{
    private AdaptiveGroundPath? _adaptiveGround;
    private GroundRouteRunner? _groundRunner;
    private List<GroundFailure>? _groundFailures = [];
    private List<Vector3>? _groundApproaches = [];
    private int _groundRepairs;
    private float _groundRepairDistance;
    private DateTime _groundRepairStartedAt;
    private bool _groundApproachOnly;
    private DateTime? _groundFailedJumpAt;
    // Injectable native boundaries, also used by the execution regression tests.
    private Func<Vector3, Vector3, bool>? _groundJumpClear = null;
    private Func<Vector3, bool>? _groundJumpAction = null;
    private Func<bool>? _groundJumping = null;
    private Func<Vector3, bool>? _groundLandingSupport = null;
    private bool GroundJumping => _groundJumping?.Invoke() ?? _flight?.IsJumping ?? false;

    private static GroundFailure? FailureAhead(Vector3 position, IReadOnlyList<Vector3> path)
    {
        var next = path.FirstOrDefault(p => Vector3.Distance(position, p) > 0.4f, position);
        var delta = next - position;
        if (delta.LengthSquared() < 0.01f) return null;
        return new GroundFailure(position, position + Vector3.Normalize(delta) * MathF.Min(1.25f, delta.Length()), false);
    }

    private void ClearAdaptiveGround()
    {
        _adaptiveGround?.Dispose(); _adaptiveGround = null;
        _groundRunner = null;
        _groundFailedJumpAt = null;
    }

    private bool CanJumpSegment(Vector3 from, Vector3 to)
    {
        if (_groundJumpClear != null) return _groundJumpClear(from, to);
        var player = _objectTable.LocalPlayer;
        return player != null && _flight != null && !_flight.IsMounted && !_flight.IsInFlight
            && Vector3.Distance(player.Position, from) <= 6 && Vector3.Distance(player.Position, to) <= 8
            && GroundTraversal.Jump(from, to, _nav.NearestPoint, GroundPathCollision.ClearJumpSegment)
            && GroundPathCollision.HasLanding(to);
    }

    private bool GroundJump(Vector3 destination)
    {
        if (_groundJumpAction != null) return _groundJumpAction(destination);
        var player = _objectTable.LocalPlayer;
        if (player == null || _flight == null) return false;
        FacingService.FaceTowards(player, destination);
        return _flight.TryGroundJump();
    }

    private GroundRouteRunner CreateGroundRunner(List<GroundLeg> route)
        => new(route, points => _nav.MoveAlong(points), _nav.Stop, () => _nav.Waypoints,
            () => _nav.PathfindInProgress, (a, b) => _groundSegmentClear?.Invoke(a, b) ?? false,
            CanJumpSegment, GroundJump, GroundStanding);

    private bool GroundStanding(Vector3 p) => _groundLandingSupport?.Invoke(p) ?? GroundPathCollision.IsStanding(p);

    private bool TryAdaptiveGround(Vector3 position, GroundFailure? failure = null)
    {
        if (_flying || _groundSegmentClear == null || !_nav.IsReady || _groundRepairs >= 8) return false;
        if (_groundRepairs == 0) _groundRepairDistance = Vector3.Distance(position, _destPosition);
        _groundFailures ??= [];
        if (failure != null) _groundFailures.Add(failure);
        ++_groundRepairs;
        ClearAdaptiveGround(); ClearHeightPath();
        _groundDetour?.Dispose(); _groundDetour = null;
        _nav.Stop();
        _plannedDestination = _destPosition;
        _adaptiveGround = new AdaptiveGroundPath(position, _destPosition,
            _destinationIsTransition ? 6 : _stopRange, _nav.NearestPoint, _nav.FindGroundPath,
            CheckPlannedGroundSegment, CanJumpSegment, _groundFailures, _groundApproaches);
        _groundRepairStartedAt = DateTime.UtcNow;
        _phase = Phase.AdaptiveSearch;
        _log.Info($"[AdaptiveGround] search start={position}, goal={_destPosition}, attempt={_groundRepairs}, excluded={_groundFailures.Count}");
        _tolk.SpeakInterrupt(AccessibilityStrings.AdaptiveGroundSearching);
        return true;
    }

    private void AdaptiveSearchUpdate()
    {
        if (!ValidateWalkContext()) return;
        var player = _objectTable.LocalPlayer!;
        var search = _adaptiveGround;
        if (search == null) { Finish(GroundPathFailure(player.Position), "adaptive search missing"); return; }
        if (_nav.IsRunning) _nav.Stop();
        if (!_nav.IsReady || Vector3.Distance(player.Position, search.Start) > 0.75f)
        { Finish(GroundPathFailure(player.Position), "adaptive search position changed or deadline expired"); return; }
        if ((DateTime.UtcNow - _groundRepairStartedAt).TotalSeconds > 30)
        {
            if (_nav.PathfindInProgress)
            { Finish(GroundPathFailure(player.Position), "external path query did not settle within repair deadline"); return; }
            search.FinishWithBestAvailable();
        }
        if (_nav.PathfindInProgress) return;
        search.Update();
        if (!search.Done) return;
        var route = search.Result;
        _groundApproachOnly = search.ApproachOnly;
        _log.Info($"[AdaptiveGround] result={route != null}, approachOnly={search.ApproachOnly}, nodes={search.Expanded}, queries={search.Queries}, reason={search.LastFailure}");
        _adaptiveGround = null;
        if (route != null) _groundRunner = CreateGroundRunner(route);
        search.Dispose();
        if (_groundRunner == null)
        {
            if (TryBridgePartialPath(player.Position) || TryTakeTrail(player.Position)) return;
            if (TryNudgeIntoTransition(Vector3.Distance(player.Position, _destPosition))) return;
            Finish(GroundPathFailure(player.Position), "adaptive mesh repair has no route"); return;
        }
        _phase = Phase.AdaptiveWalking;
        _tolk.SpeakInterrupt(AccessibilityStrings.AdaptiveGroundWalking(_targetName));
    }

    private void AdaptiveWalkingUpdate()
    {
        if (!ValidateWalkContext()) return;
        var player = _objectTable.LocalPlayer!;
        NoteGroundProgress(player.Position);
        var runner = _groundRunner;
        if (runner == null || !_nav.IsReady)
        { Finish(GroundPathFailure(player.Position), "adaptive runner or mesh missing"); return; }
        var wasJump = runner.IsJump;
        runner.Update(player.Position, _groundJumping?.Invoke() ?? _flight?.IsJumping ?? false, DateTime.UtcNow);
        if (runner.Failed)
        {
            if (runner.Failure is { Jump: true })
            {
                _groundFailedJumpAt ??= DateTime.UtcNow;
                _nav.Stop();
                var wait = DateTime.UtcNow - _groundFailedJumpAt.Value;
                if (wait > TimeSpan.FromSeconds(4))
                { Finish(AccessibilityStrings.NavigationPathBlocked, "failed jump did not settle"); return; }
                if (wait < TimeSpan.FromSeconds(0.3) || GroundJumping || !GroundStanding(player.Position)) return;
            }
            _log.Info($"[AdaptiveGround] execution failed: {runner.Reason}, edge={runner.Failure}");
            if (TryAdaptiveGround(player.Position, runner.Failure)) return;
            Finish(AccessibilityStrings.NavigationPathBlocked, "adaptive recovery budget exhausted"); return;
        }
        if (wasJump && !runner.IsJump) _tolk.SpeakInterrupt(AccessibilityStrings.AdaptiveGroundLanded);
        if (!runner.Done) return;
        _groundRunner = null;
        if (_groundApproachOnly)
        {
            (_groundApproaches ??= []).Add(player.Position);
            if (TryAdaptiveGround(player.Position)) return;
            Finish(GroundPathFailure(player.Position), "partial approach still cannot reach target"); return;
        }
        var distance = Vector3.Distance(player.Position, _destPosition);
        if (_destinationIsTransition && TryNudgeIntoTransition(distance)) return;
        // Reuse the real arrival/crossing/ceiling logic, without reporting a
        // completed partial approach or jump as arrival at the original target.
        _checkedHeightRoute = [];
        _lastMoveAt = _lastApproachAt = DateTime.UtcNow;
        _phase = Phase.Walking;
        WalkingUpdate();
    }

    private void NoteGroundProgress(Vector3 position)
    {
        var distance = Vector3.Distance(position, _destPosition);
        if (_groundRepairs > 0 && distance < _groundRepairDistance - 12)
        {
            _groundRepairs = 0;
            _groundRepairDistance = distance;
            _groundFailures?.RemoveAll(f => Vector3.Distance(f.From, position) > 20);
        }
    }
}
