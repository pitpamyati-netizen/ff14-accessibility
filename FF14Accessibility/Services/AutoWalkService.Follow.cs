using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace FF14Accessibility.Services;

public sealed partial class AutoWalkService
{
    private HeightPath? _followGroundPath;
    private Task<List<Vector3>>? _followFlightPath;
    private CancellationTokenSource? _followCancel;
    private List<Vector3>? _followRoute;
    private Vector3 _followSearchStart, _followLastPosition;
    private DateTime _followSearchStartedAt, _followLastMoveAt;
    private bool _followFlying;
    private bool _followSearchWindow;
    private int _followReplacements;
    private Func<Vector3, Vector3, bool>? _groundSegmentClear = GroundPathCollision.Clear;

    private void ClearFollowPath()
    {
        _followGroundPath?.Dispose();
        _followGroundPath = null;
        var cancel = _followCancel;
        var pending = _followFlightPath;
        _followCancel = null;
        _followFlightPath = null;
        _followRoute = null;
        if (cancel == null) return;
        cancel.Cancel();
        if (pending != null)
            _ = pending.ContinueWith(t => { _ = t.Exception; cancel.Dispose(); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        else cancel.Dispose();
    }

    private void FollowFailed(string message)
    {
        StopFollow(announce: false);
        _tolk.SpeakInterrupt(message);
    }

    private void FollowUpdate()
    {
        var player = _objectTable.LocalPlayer;
        if (player == null) { StopFollow(announce: false); return; }
        if ((ushort)_clientState.TerritoryType != _followStartTerritory)
        { FollowFailed(AccessibilityStrings.FollowStoppedZone); return; }
        var target = _objectTable.FirstOrDefault(o => o.GameObjectId == _followTargetId);
        if (target == null)
        { FollowFailed(AccessibilityStrings.FollowTargetGone(_followName)); return; }
        var from = player.Position;
        var dest = target.Position;
        if (!HeightPath.Finite(from) || !HeightPath.Finite(dest))
        { FollowFailed(AccessibilityStrings.NavigationInvalidPosition); return; }
        if (!_nav.IsReady)
        { FollowFailed(AccessibilityStrings.FollowAbortedUnavailable); return; }
        var now = DateTime.UtcNow;
        var distance = Vector3.Distance(from, dest);
        if (distance <= FollowDistance + 0.5f)
        {
            // Stop the old path too: a nearby target can walk back towards us.
            if (_followRoute != null || _followGroundPath != null || _followFlightPath != null || _nav.IsRunning)
            { ClearFollowPath(); _nav.Stop(); }
            _followLastMoveAt = _followSearchStartedAt = now;
            _followSearchWindow = false;
            _followLastPosition = from;
            return;
        }
        if (Vector3.Distance(from, _followLastPosition) >= MovementEpsilon)
        { _followLastPosition = from; _followLastMoveAt = now; _followReplacements = 0; }
        var flying = _flight is { IsInFlight: true };
        if (flying && !ShouldFly())
        { FollowFailed(AccessibilityStrings.NavigationAirborneNoRoute); return; }
        var searching = _followGroundPath != null || _followFlightPath != null;
        if (searching)
        {
            if (_nav.IsRunning) _nav.Stop();
            if ((now - _followSearchStartedAt).TotalSeconds > (flying ? FlightStartTimeoutS : 30))
            { FollowFailed(AccessibilityStrings.NavigationFollowBlocked); return; }
            if (flying != _followFlying || Vector3.Distance(dest, _lastFollowDest) > FollowRepathMove
                || Vector3.Distance(from, _followSearchStart) > 0.75f)
            { ClearFollowPath(); return; }
            if (_nav.PathfindInProgress) return;
            List<Vector3>? result;
            if (_followGroundPath is { } ground)
            {
                ground.Update();
                if (!ground.Done) return;
                result = ground.Result == null ? null : new List<Vector3>(ground.Result);
            }
            else
            {
                var task = _followFlightPath!;
                if (!task.IsCompleted) return;
                result = !task.IsCanceled && !task.IsFaulted
                    && ValidFlightPath(task.Result, _lastFollowDest, FollowDistance)
                    ? new List<Vector3>(task.Result) : null;
                _ = task.Exception;
            }
            ClearFollowPath();
            if (result == null)
            { FollowFailed(flying ? AccessibilityStrings.NavigationFlightUnavailable : AccessibilityStrings.GroundPathUnavailable); return; }
            _followRoute = new List<Vector3>(result);
            if (!_nav.MoveAlong(new List<Vector3>(result), flying))
            { FollowFailed(AccessibilityStrings.FollowAbortedUnavailable); return; }
            _followSearchStartedAt = now;
            _followSearchWindow = false;
            _followLastMoveAt = now;
            return;
        }
        if ((now - _followLastMoveAt).TotalSeconds > StallS)
        { FollowFailed(AccessibilityStrings.NavigationFollowBlocked); return; }
        if (!flying && _followRoute != null && _groundSegmentClear != null && _nav.Waypoints is { Count: > 0 } steps)
        {
            var delta = steps[0] - from;
            var length = delta.Length();
            if (!_groundSegmentClear(from, length > 1.5f ? from + delta * (1.5f / length) : steps[0]))
            { FollowFailed(AccessibilityStrings.NavigationPathBlocked); return; }
        }
        if (_followRoute != null && (_nav.PathfindInProgress
            || !HeightPath.IsRemainingPath(_followRoute, _nav.Waypoints)))
        {
            _nav.Stop(); ClearFollowPath();
            if (++_followReplacements > MaxReengages)
            { FollowFailed(AccessibilityStrings.NavigationFollowBlocked); return; }
        }
        if ((now - _lastFollowPathAt).TotalSeconds < FollowRepathIntervalS) return;
        if (_followRoute != null && flying == _followFlying && _nav.IsRunning
            && Vector3.Distance(dest, _lastFollowDest) < FollowRepathMove) return;
        _nav.Stop(); ClearFollowPath();
        _lastFollowDest = dest;
        _lastFollowPathAt = now;
        _followSearchStart = from;
        _followFlying = flying;
        if (!_followSearchWindow)
        { _followSearchStartedAt = now; _followSearchWindow = true; }
        if (flying)
        {
            _followCancel = new CancellationTokenSource();
            _followFlightPath = _nav.FindPath(from, dest, true, _followCancel.Token);
            if (_followFlightPath == null) FollowFailed(AccessibilityStrings.FollowAbortedUnavailable);
        }
        else _followGroundPath = new HeightPath(from, dest, FollowDistance,
            _nav.NearestPoint, _nav.FindGroundPath, _groundSegmentClear);
    }
}
