using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace FF14Accessibility.Services;

public sealed partial class AutoWalkService
{
    private HeightPath? _heightPath;
    private List<Vector3>? _checkedHeightRoute;

    private void ClearHeightPath()
    {
        _heightPath?.Dispose();
        _heightPath = null;
        _checkedHeightRoute = null;
    }

    private void HeightSearchUpdate()
    {
        if (!ValidateWalkContext()) return;
        var player = _objectTable.LocalPlayer;
        if (player == null) { Finish(null, "height: player missing"); return; }
        if ((ushort)_clientState.TerritoryType != _startTerritory)
        { Finish(AccessibilityStrings.ArrivedNewZone, "height: zone changed"); return; }
        var search = _heightPath;
        if (search == null) { Finish(null, "height: search missing"); return; }
        // Stop any late SimpleMove result from an earlier command before using
        // the position from which this query-only search was started.
        if (_nav.IsRunning) _nav.Stop();
        if (Vector3.Distance(player.Position, search.Start) > 0.75f
            || (DateTime.UtcNow - _startedAt).TotalSeconds > 30 || !_nav.IsReady)
        {
            Finish(GroundPathFailure(player.Position),
                "height: changed position, timeout or mesh unavailable");
            return;
        }
        if (_nav.PathfindInProgress) return;
        search.Update();
        if (!search.Done) return;
        var result = search.Result;
        var queries = search.Queries;
        var failure = search.LastFailure;
        _heightPath = null;
        search.Dispose();
        if (result == null)
        {
            _log.Info($"[HeightPath] no supported route; queries={queries}; target=({Fmt(_destPosition)}); reason={failure}");
            if (TryAdaptiveGround(player.Position)) return;
            // Keep the existing, measured/recorded crossings available; never
            // manufacture a direct jump between disconnected floors.
            if (TryBridgePartialPath(player.Position)) return;
            if (TryTakeTrail(player.Position)) return;
            if (_destinationIsTransition && Vector3.Distance(player.Position, _destPosition) <= 6
                && TryNudgeIntoTransition(Vector3.Distance(player.Position, _destPosition))) return;
            Finish(GroundPathFailure(player.Position),
                "height: no continuous route to target floor");
            return;
        }
        _nav.Stop();
        _checkedHeightRoute = new List<Vector3>(result);
        if (!_nav.MoveAlong(new List<Vector3>(result)))
        { Finish(AccessibilityStrings.AutoWalkAbortedNoResponse, "height: move rejected"); return; }
        _phase = Phase.Walking;
        _lastMoveAt = _lastApproachAt = _startedAt = DateTime.UtcNow;
        _lastPosition = player.Position;
        _pathQuiet = false;
        _partialPathChecked = true;
        _log.Info($"[HeightPath] walking checked route; queries={queries}; points={result.Count}; " +
            string.Join(" -> ", result.Select(p => $"({Fmt(p)})")));
        _tolk.SpeakInterrupt(AccessibilityStrings.WalkingTo(_targetName));
    }
}
