using System.Numerics;
using FF14Accessibility.Services;

namespace FF14Accessibility;

public sealed partial class Plugin
{
    private uint _resolvedWalkingMap;
    private PendingNavigation? _pendingNavigation;
    private LocalTransfer? _walkingTransfer;
    private sealed record PendingNavigation(NavigationPathCheck Check, string Name, float Stop, bool Transition,
        bool Guide, uint Map, uint Territory, Vector3 From, QuestDestination? Quest, ObjectDestination? Object,
        PlaceDestination? Place, ObjectDestination? Live, bool Track);

    private bool QueueNavigationCheck(Vector3 position, string name, float stop, bool transition, bool guide)
    {
        var area = _navigation.SelectedQuestDestination is { } quest && QuestAreaPoint.IsSearchArea(quest)
            && quest.TerritoryTypeId == ClientState.TerritoryType && _places.AreSameMap(quest.MapId, ClientState.MapId)
            ? quest : null;
        if (!_resolvedCanTrackObject && !transition && area == null) return false;
        var player = ObjectTable.LocalPlayer;
        if (player == null) return false;
        CancelNavigationCheck();
        var choices = new List<NavigationPathCheck.Choice> { new(position) };
        if (transition && _resolvedWalkingMap != 0)
            choices.AddRange(_zoneBorders.Targets(_resolvedWalkingMap, player.Position, _autoWalk.ProbeReachable)
                .Where(p => Vector3.Distance(p, position) >= 0.5f).Select(p => new NavigationPathCheck.Choice(p)));
        else
        {
            if (area != null)
                choices.AddRange(QuestAreaPoint.ReachablePoints(area.Position, area.Radius, stop, _autoWalk.ProbeReachable,
                    p => _places.MatchesKnownMap(p, area.MapId)).Take(32)
                    .Where(p => Vector3.Distance(p, position) >= 0.5f).Select(p => new NavigationPathCheck.Choice(p)));
            foreach (var transfer in _places.LocalTransfers().OrderBy(t => Vector3.Distance(t.Position, player.Position)))
            {
                var actor = SelectionObjectResolver.Linked(ObjectTable, transfer.BaseId, transfer.LevelType, transfer.Position);
                if (actor is not { IsTargetable: true }) continue;
                var approach = SelectionObjectResolver.Approach(actor.Position, _autoWalk.ProbeReachable);
                if (approach != null) choices.Add(new(approach.Value, transfer));
            }
        }
        var check = new NavigationPathCheck(player.Position, position, transition ? 5.2f : stop,
            choices, _routes.RequestPath);
        _pendingNavigation = new(check, name, stop, transition, guide, ClientState.MapId,
            ClientState.TerritoryType, player.Position, _navigation.SelectedQuestDestination,
            _navigation.SelectedObjectDestination, _navigation.SelectedPlaceDestination,
            _resolvedNavigationObject is { } live ? new(live.GameObjectId, name, live.Position, live.ObjectKind, live.BaseId) : null,
            _resolvedCanTrackObject);
        _tolk.SpeakInterrupt(AccessibilityStrings.ComputingRouteTo(name));
        check.Poll();
        return true;
    }

    private bool CancelNavigationCheck()
    {
        if (_pendingNavigation is not { } pending) return false;
        pending.Check.Cancel(); _pendingNavigation = null;
        return true;
    }

    private void PollNavigationCheck()
    {
        if (_pendingNavigation is not { } pending) return;
        var player = ObjectTable.LocalPlayer;
        if (player == null || ClientState.MapId != pending.Map || ClientState.TerritoryType != pending.Territory
            || pending.Quest != _navigation.SelectedQuestDestination || pending.Object != _navigation.SelectedObjectDestination
            || pending.Place != _navigation.SelectedPlaceDestination || Vector3.Distance(player.Position, pending.From) > 3f
            || !_autoWalk.Navmesh.IsReady || !GameWindowFocus.IsActive || _menu.IsOpen || _hotbar.IsSkillMenuOpen
            || _uiReader.IsTableReading || _uiReader.IsShopQuantityEditing || _uiReader.IsSystemVolumeEditing
            || _uiReader.HasActiveMenu || _uiReader.BlockingFocusedAddonForPlayerMenu() != null || _armouryList.OwnsInput
            || _autoWalk.IsWalking || _navigation.IsWalkGuideActive || _transitions.IsActive || _textInputActive)
        { CancelNavigationCheck(); return; }
        var current = pending.Live != null ? SelectionObjectResolver.Exact(ObjectTable, pending.Live) : null;
        if (pending.Live != null && current == null)
        { CancelNavigationCheck(); _tolk.SpeakInterrupt(AccessibilityStrings.SelectedObjectMissing(pending.Name)); return; }
        if (current != null && Vector3.Distance(current.Position, pending.Live!.Position) > 0.75f)
        {
            CancelNavigationCheck();
            var floor = SelectionObjectResolver.Approach(current.Position, _autoWalk.ProbeReachable);
            if (floor == null) { _tolk.SpeakInterrupt(AccessibilityStrings.NavigationPathUnavailable(pending.Name)); return; }
            _resolvedNavigationObject = current; _resolvedCanTrackObject = true;
            QueueNavigationCheck(floor.Value, pending.Name, pending.Stop, pending.Transition, pending.Guide);
            return;
        }
        pending.Check.Poll();
        if (!pending.Check.Completed) return;
        _pendingNavigation = null;
        var result = pending.Check.Result;
        Log.Info($"[RouteCheck] map={pending.Map}, goal={pending.Name}, requests={pending.Check.Requests}, "
            + $"result={result?.Position.ToString() ?? "unreachable"}, transfer={result?.Transfer?.WarpId ?? 0}");
        if (result == null)
        {
            if (_bridges.FindCrossing(player.Position, pending.Check.Goal, out _, out _) == null)
            { _tolk.SpeakInterrupt(AccessibilityStrings.NavigationPathUnavailable(pending.Name)); return; }
            result = new(pending.Check.Goal);
        }
        _resolvedNavigationObject = current; _resolvedCanTrackObject = pending.Track;
        var name = pending.Name; var stop = pending.Stop; var transition = pending.Transition;
        if (result.Transfer is { } transfer)
        {
            var live = SelectionObjectResolver.Linked(ObjectTable, transfer.BaseId, transfer.LevelType, transfer.Position);
            if (live is not { IsTargetable: true } || Vector3.Distance(live.Position, result.Position) > AutoWalkService.StopRange + 0.3f)
            { _tolk.SpeakInterrupt(AccessibilityStrings.NavigationPathUnavailable(name)); return; }
            _navigation.SetLocalTransfer(transfer);
            _walkingTransfer = transfer;
            _resolvedNavigationObject = live; _resolvedCanTrackObject = true;
            name = _objectNames.Resolve(live) ?? live.Name.TextValue;
            stop = AutoWalkService.StopRange; transition = false;
            _tolk.SpeakInterrupt(AccessibilityStrings.LocalTransferRoute(pending.Name, name));
        }
        if (pending.Guide) ApplyResolvedGuide(result.Position, name, stop);
        else ApplyResolvedWalk(result.Position, name, stop, transition);
    }

    private void PollLocalTransferArrival()
    {
        if (_walkingTransfer is not { } transfer) return;
        if (!CurrentWalkingSelection) { _walkingTransfer = null; return; }
        if (ObjectTable.LocalPlayer is not { } player || !transfer.HasArrived(player.Position)) return;
        _autoWalk.StopQuiet(); _navigation.StopWalkGuideQuiet();
        _walkingObject = null; _awaitingQuestObject = false; _needsObjectTracking = false;
        _walkingTransfer = null; _navigation.SetLocalTransfer(null);
        Log.Info($"[RouteCheck] Local transfer {transfer.WarpId} arrival reached on map {transfer.MapId}.");
    }
}
