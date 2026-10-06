using System.Numerics;
using FF14Accessibility.Services;

namespace FF14Accessibility;

public sealed partial class Plugin
{
    // Quest routing sits above the preserved movement service. Every key press
    // reads the current objective and resolves only the next local leg.
    private MarkerResolve TryResolveSelectedDestination(out Vector3 position, out string name,
        out float stopRange, out bool heightIsGuess, out bool isZoneTransition, bool readout = false)
    {
        if (_navigation.SelectedQuestDestination is not { } selection
            || selection.QuestId == 0 && selection.NativeMarkerId == 0)
            return readout
                ? TryResolveDestinationReadout(out position, out name, out stopRange, out heightIsGuess, out isZoneTransition)
                : TryResolveMarkerDestination(out position, out name, out stopRange, out heightIsGuess, out isZoneTransition);
        position = default; name = string.Empty; stopRange = _config.AutoWalkPlaceStopRange;
        heightIsGuess = false; isZoneTransition = false;
        if (!_navigation.RefreshSelectedQuest())
        {
            _tolk.SpeakInterrupt(AccessibilityStrings.QuestGoalUpdating);
            return MarkerResolve.Failed;
        }
        var quest = _navigation.GetSelectedQuestTravelGoal()!;
        var plan = QuestNavigationPlan.Resolve(quest, ClientState.TerritoryType, ClientState.MapId,
            id => _places.FindFirstHopToMap(id, out _), _places.FindLocalEntranceOnRoute, _places.AreSameMap);
        if (plan == null)
        {
            var duty = FindQuestDutyFinder(quest);
            var teleport = FindQuestTeleport(quest);
            _tolk.SpeakInterrupt(duty != null ? AccessibilityStrings.QuestDutyFinderRoute(quest.QuestName, duty)
                : teleport != null
                ? AccessibilityStrings.QuestTeleportRoute(quest.QuestName, teleport.Name, _places.GetMapName(quest.MapId))
                : AccessibilityStrings.QuestTravelUnavailable(quest.QuestName, _places.GetMapName(quest.MapId)));
            return MarkerResolve.Failed;
        }
        name = plan.Name;
        heightIsGuess = plan.HeightIsGuess;
        isZoneTransition = plan.IsBorder;
        stopRange = plan.IsBorder ? _config.AutoWalkTransitionStopRange
            : plan.Entrance != null ? _config.AutoWalkPlaceStopRange
            : quest.Role == QuestMarkerRole.QuestTrigger ? _config.AutoWalkPlaceStopRange
            : MathF.Max(_config.AutoWalkPlaceStopRange, MathF.Min(5f, quest.Radius));
        var point = plan.Position;
        if (plan.HeightIsGuess) point = point with { Y = ObjectTable.LocalPlayer?.Position.Y ?? point.Y };
        if (!readout)
        {
            if (plan.IsBorder)
            {
                var border = _zoneBorders.FindBorderPoint(plan.NextMapId, ObjectTable.LocalPlayer?.Position ?? point,
                    _autoWalk.ProbeReachable);
                isZoneTransition = border != null;
                point = border ?? point;
            }
            var floor = _autoWalk.ResolveReachablePoint(point) ?? _autoWalk.ResolveFloorPoint(point);
            if (floor == null)
            {
                _tolk.SpeakInterrupt(AccessibilityStrings.NoWalkablePointAt(name));
                return MarkerResolve.Failed;
            }
            // A known NPC or interior location must retain its floor. Do not
            // accept a floor snap to an outside NPC 130 metres above the guild.
            if (!plan.HeightIsGuess && MathF.Abs(floor.Value.Y - point.Y) > 5f)
            {
                _tolk.SpeakInterrupt(AccessibilityStrings.NoWalkablePointAt(name));
                return MarkerResolve.Failed;
            }
            point = floor.Value;
        }
        position = point;
        return MarkerResolve.Resolved;
    }

    private unsafe QuestTeleportChoice? FindQuestTeleport(QuestDestination quest)
    {
        var state = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
        if (state == null || !ClientState.IsLoggedIn) return null;
        try { return QuestTravelHints.FindTeleport(DataManager, _places, quest, id => state->IsAetheryteUnlocked(id)); }
        catch (Exception ex) { Log.Warning($"[QuestRoute] Teleport availability: {ex.Message}"); return null; }
    }

    private unsafe string? FindQuestDutyFinder(QuestDestination quest)
    {
        var state = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
        if (state == null || !ClientState.IsLoggedIn) return null;
        var choices = DataManager.GetExcelSheet<Lumina.Excel.Sheets.InstanceContent>()
            .Where(i => i.ContentFinderCondition.ValueNullable?.TerritoryType.RowId == quest.TerritoryTypeId).ToArray();
        if (choices.Length != 1) return null;
        try
        {
            if (!FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.IsInstanceContentUnlocked(choices[0].RowId)) return null;
            return choices[0].ContentFinderCondition.ValueNullable?.Name.ExtractText();
        }
        catch (Exception ex) { Log.Warning($"[QuestRoute] Duty availability: {ex.Message}"); return null; }
    }

    private long _nextQuestRefresh;
    private void PollSelectedQuest()
    {
        if (_navigation.SelectedQuestDestination is not { } previous || previous.QuestId == 0 && previous.NativeMarkerId == 0) return;
        var tick = System.Diagnostics.Stopwatch.GetTimestamp();
        if (tick < _nextQuestRefresh) return;
        _nextQuestRefresh = tick + System.Diagnostics.Stopwatch.Frequency / 2;
        var refreshed = _navigation.RefreshSelectedQuest();
        var current = _navigation.SelectedQuestDestination;
        if (!refreshed || current?.ObjectiveLevelId != previous.ObjectiveLevelId || current?.MapId != previous.MapId
            || current?.Position != previous.Position)
        {
            _autoWalk.StopQuiet();
            _navigation.StopWalkGuideQuiet();
            _transitions.Stop(silent: true);
        }
    }
}
