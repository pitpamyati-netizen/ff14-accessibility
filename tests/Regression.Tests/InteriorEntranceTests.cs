using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FF14Accessibility.Services;

namespace Regression.Tests;

public sealed class InteriorEntranceTests
{
    [Fact]
    public void EntranceUsesSourceObjectPositionAndNeverTheArrivalPosition()
    {
        var entrance = InteriorEntranceService.Validate(2, 132, 8, 1000423, new(232, 2, 46),
            204, 68, 204, 132, 204);
        Assert.NotNull(entrance);
        Assert.Equal(new Vector3(232, 2, 46), entrance.Position);
        Assert.Equal(2u, entrance.SourceMapId);
        Assert.Equal(68u, entrance.TargetMapId);
    }

    [Theory]
    [InlineData(0, 132, 8, 1000423, 204, 68, 204, 132, 204)]
    [InlineData(2, 0, 8, 1000423, 204, 68, 204, 132, 204)]
    [InlineData(2, 132, 9, 1000423, 204, 68, 204, 132, 204)]
    [InlineData(2, 132, 8, 0, 204, 68, 204, 132, 204)]
    [InlineData(2, 132, 8, 1000423, 0, 68, 204, 132, 204)]
    [InlineData(2, 132, 8, 1000423, 204, 0, 204, 132, 204)]
    [InlineData(2, 132, 8, 1000423, 204, 2, 204, 132, 204)]
    [InlineData(2, 132, 8, 1000423, 204, 68, 133, 132, 204)]
    [InlineData(2, 132, 8, 1000423, 204, 68, 204, 133, 204)]
    [InlineData(2, 132, 8, 1000423, 204, 68, 204, 132, 133)]
    [InlineData(2, 132, 8, 1000423, 132, 68, 132, 132, 132)]
    public void InvalidOrConflictingLinksDoNotCreateRoutes(uint sourceMap, uint sourceTerritory,
        byte type, uint baseId, uint warpTerritory, uint targetMap, uint arrivalTerritory,
        uint sourceMapTerritory, uint targetMapTerritory)
        => Assert.Null(InteriorEntranceService.Validate(sourceMap, sourceTerritory, type, baseId, Vector3.Zero,
            warpTerritory, targetMap, arrivalTerritory, sourceMapTerritory, targetMapTerritory));

    [Fact]
    public void NonFiniteCoordinatesNeverBecomeAWalkDestination()
        => Assert.Null(InteriorEntranceService.Validate(2, 132, 8, 1000423, new(float.NaN, 2, 46),
            204, 68, 204, 132, 204));

    [Fact]
    public void ForeignQuestTargetsExactGuardInsteadOfNearbyNpcEnemyOrUntargetableClone()
    {
        var guard = Obj(1000423, ObjectKind.EventNpc, new(232, 2, 46));
        var wrongNpc = Obj(1000205, ObjectKind.EventNpc, guard.Position);
        var enemy = Obj(1000423, ObjectKind.BattleNpc, guard.Position);
        var clone = Obj(1000423, ObjectKind.EventNpc, guard.Position, false);
        var distantClone = Obj(1000423, ObjectKind.EventNpc, new(300, 2, 46));
        var nav = Harness([wrongNpc, enemy, clone, distantClone, guard]);
        Assert.Same(guard, Resolve(nav));
    }

    [Fact]
    public void MissingEntranceObjectDoesNotSelectAnotherNpc()
        => Assert.Null(Resolve(Harness([Obj(1000205, ObjectKind.EventNpc, new(232, 2, 46))])));

    [Fact]
    public void TeleportInvalidatesLocalEntranceBeforeConfirm()
        => Assert.Null(Resolve(Harness([Obj(1000423, ObjectKind.EventNpc, new(232, 2, 46))], map: 3)));

    private static IGameObject? Resolve(NavigationService nav) => (IGameObject?)typeof(NavigationService)
        .GetMethod("ResolveSelectionObject", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(nav, null);

    private static IGameObject Obj(uint id, ObjectKind kind, Vector3 position, bool targetable = true)
        => ChatPlayerTests.Proxy.Of<IGameObject>(m => m.Name switch
        {
            "get_BaseId" => id, "get_ObjectKind" => kind, "get_Position" => position,
            "get_IsTargetable" => targetable, _ => throw new NotSupportedException(m.Name),
        });

    private static NavigationService Harness(IGameObject[] objects, uint map = 2)
    {
        var client = ChatPlayerTests.Proxy.Of<IClientState>(m => m.Name switch
        {
            "get_MapId" => map, "get_TerritoryType" => (ushort)132,
            _ => throw new NotSupportedException(m.Name),
        });
        var table = ChatPlayerTests.Proxy.Of<IObjectTable>(m => m.Name == "GetEnumerator"
            ? ((IEnumerable<IGameObject>)objects).GetEnumerator() : throw new NotSupportedException(m.Name));
        var places = new PlacesService(null!, client, null!);
        var entries = (InteriorEntranceService)typeof(PlacesService)
            .GetField("_interiorEntrances", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(places)!;
        typeof(InteriorEntranceService).GetField("_all", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(entries, new List<InteriorEntrance> { new(2, 68, 1000423, 8, new(232, 2, 46)) });
        var nav = (NavigationService)RuntimeHelpers.GetUninitializedObject(typeof(NavigationService));
        foreach (var pair in new Dictionary<string, object> { ["_places"] = places,
            ["_clientState"] = client, ["_objectTable"] = table })
            typeof(NavigationService).GetField(pair.Key, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(nav, pair.Value);
        typeof(NavigationService).GetProperty("SelectedQuestDestination")!.SetValue(nav,
            new QuestDestination("Защитить Хранителя", "", new(0, 0.5f, -4.4f), 1, 204, 68, false, QuestKind.MainStory, 14));
        return nav;
    }
}
