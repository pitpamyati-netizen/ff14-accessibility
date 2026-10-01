using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace FF14Accessibility.Services;

public sealed partial class AutoWalkService
{
    private Task<List<Vector3>>? _flightPath;
    private CancellationTokenSource? _flightPathCancel;
    private Vector3 _flightPathStart;

    private void ClearFlightPath()
    {
        var cancel = _flightPathCancel;
        var pending = _flightPath;
        _flightPathCancel = null;
        _flightPath = null;
        if (cancel == null) return;
        cancel.Cancel();
        if (pending != null)
            _ = pending.ContinueWith(t => { _ = t.Exception; cancel.Dispose(); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        else cancel.Dispose();
    }

    private void FlightSearchUpdate()
    {
        if (!ValidateWalkContext()) return;
        var player = _objectTable.LocalPlayer!;
        var pending = _flightPath;
        if (_nav.IsRunning) _nav.Stop();
        var waited = (DateTime.UtcNow - _startedAt).TotalSeconds;
        if (_flight is not { IsInFlight: true } || !_nav.IsReady
            || Vector3.Distance(player.Position, _flightPathStart) > 1
            || waited > FlightStartTimeoutS || pending == null)
        { Finish(AccessibilityStrings.NavigationFlightUnavailable, "flight search invalidated or timed out"); return; }
        if (!pending.IsCompleted)
        {
            if (!_flightSearchAnnounced && waited > FlightSearchNoticeS)
            { _flightSearchAnnounced = true; _tolk.Speak(AccessibilityStrings.FlightPathSearching); }
            return;
        }
        if (_nav.PathfindInProgress) return;
        if (pending.IsCanceled || pending.IsFaulted)
        { _ = pending.Exception; Finish(AccessibilityStrings.NavigationFlightUnavailable, "flight query failed"); return; }
        var route = new List<Vector3>(pending.Result);
        if (!ValidFlightPath(route, _destPosition, _stopRange))
        { Finish(AccessibilityStrings.NavigationFlightUnavailable, "flight query returned a partial route"); return; }
        ClearFlightPath();
        _checkedHeightRoute = new List<Vector3>(route);
        if (!_nav.MoveAlong(new List<Vector3>(route), fly: true))
        { Finish(AccessibilityStrings.AutoWalkAbortedNoResponse, "flight move rejected"); return; }
        _phase = Phase.Walking;
        _lastPosition = player.Position;
        _lastMoveAt = _lastApproachAt = DateTime.UtcNow;
        _pathQuiet = false;
        _log.Info($"[FlightPath] walking checked flight route, points={route.Count}");
    }

    internal static bool ValidFlightPath(IReadOnlyList<Vector3> path, Vector3 goal, float range)
    {
        if (!HeightPath.Finite(goal) || !float.IsFinite(range) || range <= 0
            || path.Count < 2 || path.Count > 8192) return false;
        foreach (var p in path) if (!HeightPath.Finite(p)) return false;
        // The final point can be appended to a partial volume route too. The
        // last actual voxel must be close enough to the requested destination.
        return Vector3.Distance(path[^1], goal) <= 0.75f
            && Vector3.Distance(path[^2], goal) <= MathF.Max(range, 2.5f);
    }
}
