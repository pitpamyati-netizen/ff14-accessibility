using System.Reflection;
using System.Runtime.CompilerServices;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FF14Accessibility;
using FF14Accessibility.Services;

namespace Regression.Tests;

public sealed class WorldObjectConfirmTests
{
    [Theory]
    [InlineData(ObjectKind.EventObj)]
    [InlineData(ObjectKind.Treasure)]
    [InlineData(ObjectKind.GatheringPoint)]
    [InlineData(ObjectKind.Aetheryte)]
    public void ExplicitPropChoiceOwnsConfirmWhenCombatStillHasAnEnemyTarget(ObjectKind kind)
    {
        var nav = CombatNavigation();
        typeof(NavigationService).GetProperty("SelectedObjectDestination")!
            .SetValue(nav, new ObjectDestination(0x10039A457, "Nest", Vector3.Zero, kind));
        Assert.True(nav.HasWorldObjectConfirmSelection());
    }

    [Fact]
    public void CombatEnemyWithoutExplicitPropChoiceKeepsNativeConfirm()
        => Assert.False(CombatNavigation().HasWorldObjectConfirmSelection());

    private static NavigationService CombatNavigation()
    {
        var nav = (NavigationService)RuntimeHelpers.GetUninitializedObject(typeof(NavigationService));
        var player = ChatPlayerTests.Proxy.Of<IPlayerCharacter>(m => m.Name == "get_StatusFlags"
            ? StatusFlags.InCombat : throw new NotSupportedException(m.Name));
        var objects = ChatPlayerTests.Proxy.Of<IObjectTable>(m => m.Name == "get_LocalPlayer"
            ? player : throw new NotSupportedException(m.Name));
        var targets = DispatchProxy.Create<ITargetManager, NavigationInputTests.TargetProxy>();
        ((NavigationInputTests.TargetProxy)targets).Hard = Object(0x400162A1, ObjectKind.BattleNpc);
        typeof(NavigationService).GetField("_objectTable", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(nav, objects);
        typeof(NavigationService).GetField("_targetManager", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(nav, targets);
        return nav;
    }

    [Fact]
    public void YarzonNestConfirmClearsNearbyEnemySoftTargetAndInteractsWithExactNest()
    {
        var selected = Object(0x10039A457, ObjectKind.EventObj);
        var sameNameNest = Object(0x10039A456, ObjectKind.EventObj);
        var enemy = Object(0x400162A1, ObjectKind.BattleNpc);
        var targets = DispatchProxy.Create<ITargetManager, NavigationInputTests.TargetProxy>();
        var state = (NavigationInputTests.TargetProxy)targets;
        state.Accepts = true;
        state.Hard = selected;
        state.Soft = enemy;
        var exact = BrowserTargetSelection.FindExact([enemy, sameNameNest, selected], 0x10039A457);
        Assert.Same(selected, exact);
        IGameObject? interacted = null;
        Assert.True(BrowserTargetSelection.ConfirmWorldObject(targets, exact!, o => interacted = o));
        Assert.Same(selected, interacted);
        Assert.Same(selected, targets.Target);
        Assert.Null(targets.SoftTarget);
    }

    [Theory]
    [InlineData(false, true, ObjectKind.EventObj)]
    [InlineData(true, false, ObjectKind.EventObj)]
    [InlineData(true, true, ObjectKind.BattleNpc)]
    [InlineData(true, true, ObjectKind.Pc)]
    public void RejectionUnavailableObjectOrCombatTargetNeverRequestsInteraction(bool accepts, bool targetable, ObjectKind kind)
    {
        var obj = Object(11, kind, targetable);
        var targets = DispatchProxy.Create<ITargetManager, NavigationInputTests.TargetProxy>();
        ((NavigationInputTests.TargetProxy)targets).Accepts = accepts;
        var calls = 0;
        Assert.False(BrowserTargetSelection.ConfirmWorldObject(targets, obj, _ => calls++));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void UnloadedNestIsNeverReplacedWithAnotherNestOrEnemy()
    {
        var enemy = Object(0x400162A1, ObjectKind.BattleNpc);
        var otherNest = Object(0x10039A456, ObjectKind.EventObj);
        Assert.Null(BrowserTargetSelection.FindExact([enemy, otherNest], 0x10039A457));
        Assert.Null(BrowserTargetSelection.FindExact([enemy], 0));
    }

    [Fact]
    public void HeldConfirmCannotActivateTheWindowItJustOpened()
    {
        var (plugin, keys) = FollowTargetKeyTests.CreateInput();
        var method = typeof(Plugin).GetMethod("SuppressOwnedWorldConfirmHold", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var owned = typeof(Plugin).GetField("_worldConfirmKeyOwned", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var edges = typeof(Plugin).GetMethod("UpdateKeyEdges", BindingFlags.Instance | BindingFlags.NonPublic)!;
        owned.SetValue(plugin, true);
        keys.Down[0x60] = true;
        edges.Invoke(plugin, null);
        method.Invoke(plugin, null);
        Assert.False(keys.Down[0x60]);
        Assert.True((bool)owned.GetValue(plugin)!);
        // Native interaction has opened InventoryEventGrid; ownership survives.
        keys.Down[0x60] = true;
        edges.Invoke(plugin, null);
        method.Invoke(plugin, null);
        Assert.False(keys.Down[0x60]);
        edges.Invoke(plugin, null);
        method.Invoke(plugin, null);
        Assert.False((bool)owned.GetValue(plugin)!);
        keys.Down[0x60] = true;
        edges.Invoke(plugin, null);
        method.Invoke(plugin, null);
        Assert.True(keys.Down[0x60]); // A new press belongs to the open window.
    }

    private static IGameObject Object(ulong id, ObjectKind kind, bool targetable = true)
    {
        var obj = DispatchProxy.Create<IGameObject, WorldObjectProxy>();
        var state = (WorldObjectProxy)obj;
        state.Id = id; state.Kind = kind; state.Targetable = targetable;
        return obj;
    }

    public class WorldObjectProxy : DispatchProxy
    {
        public ulong Id;
        public ObjectKind Kind;
        public bool Targetable;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_GameObjectId" => Id, "get_ObjectKind" => Kind, "get_IsTargetable" => Targetable,
            _ => throw new NotSupportedException(method?.Name),
        };
    }
}
