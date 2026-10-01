using System;
using System.Linq;
using System.Numerics;

namespace FF14Accessibility.Services;

public sealed partial class AutoWalkService
{
    private Vector3 _plannedDestination;
    private int _targetRepaths;

    private bool ValidateWalkContext()
    {
        if ((ushort)_clientState.TerritoryType != _startTerritory)
        { Finish(AccessibilityStrings.ArrivedNewZone, "zone changed during navigation"); return false; }
        var player = _objectTable.LocalPlayer;
        if (player == null)
        { Finish(null, "player missing during navigation"); return false; }
        if (!HeightPath.Finite(player.Position) || !HeightPath.Finite(_destPosition))
        { Finish(AccessibilityStrings.NavigationInvalidPosition, "invalid live position"); return false; }
        if (_targetId == 0 || _phase == Phase.Landing) return true;
        var target = _objectTable.FirstOrDefault(o => o.GameObjectId == _targetId);
        if (target == null)
        { Finish(AccessibilityStrings.NavigationTargetGone(_targetName), "target disappeared"); return false; }
        if (!HeightPath.Finite(target.Position))
        { Finish(AccessibilityStrings.NavigationInvalidPosition, "invalid target position"); return false; }
        _destPosition = target.Position;
        if (_phase is Phase.HeightSearch or Phase.Starting or Phase.Walking
            && Vector3.Distance(_plannedDestination, _destPosition) > 1.5f)
        {
            if (++_targetRepaths > 8)
            { Finish(AccessibilityStrings.NavigationTargetMoving, "one-shot target keeps moving"); return false; }
            Begin(_destPosition, _targetName, _stopRange, _targetId, fresh: false);
            return false;
        }
        return true;
    }

    private string GroundPathFailure(Vector3 player)
        => !_destinationHeightIsGuess && MathF.Abs(_destPosition.Y - player.Y) >= 3
            ? AccessibilityStrings.HeightPathUnavailable(_destPosition.Y - player.Y)
            : AccessibilityStrings.GroundPathUnavailable;

    private bool CheckUpcomingGroundSegment(Vector3 from, System.Collections.Generic.IReadOnlyList<Vector3> path)
    {
        if (_flying || _groundSegmentClear == null || path.Count == 0) return true;
        var delta = path[0] - from;
        var length = delta.Length();
        var to = length > 1.5f ? from + delta * (1.5f / length) : path[0];
        if (_groundSegmentClear(from, to)) return true;
        Finish(AccessibilityStrings.NavigationPathBlocked, "live collision blocks next step");
        return false;
    }
}
