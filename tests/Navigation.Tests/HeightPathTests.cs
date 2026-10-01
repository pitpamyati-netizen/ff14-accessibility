using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FF14Accessibility;
using FF14Accessibility.Services;

namespace Navigation.Tests;

public class HeightPathTests
{
    private static readonly Vector3 Player = new(-151.72122f, 4.687668f, 39.49357f);
    private static readonly Vector3 Poacher = new(-151.72046f, 18.631226f, 39.53601f);

    [Fact]
    public void BrowserDoesNotReportSouthForTheLoggedNpcAlmostDirectlyAbove()
    {
        var navigation = (NavigationService)RuntimeHelpers.GetUninitializedObject(typeof(NavigationService));
        var player = DestinationReadoutTests.StrictProxy.Of<IGameObject>(m => m.Name == "get_Position"
            ? Player : throw new Exception("A vertical destination must not use player rotation"));
        var method = typeof(NavigationService).GetMethod("CalculateDirection", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(AccessibilityStrings.TargetSameHorizontalPosition, method.Invoke(navigation, [player, Poacher, "test"]));
    }

    [Fact]
    public void RejectsBothFalsePathsFromThePlayersLog()
    {
        Assert.False(HeightPath.ValidShape([Player, Poacher, Poacher], Player, Poacher));
        Assert.False(HeightPath.ValidShape([Player, new(Poacher.X, 5, Poacher.Z), Poacher], Player, Poacher));
        // The older second-to-last-point check reported zero shortfall here.
        Assert.Equal(0, Vector3.Distance(new[] { Player, Poacher, Poacher }[^2], Poacher));
    }

    [Theory]
    [InlineData(14)]
    [InlineData(-14)]
    public void RejectsVerticalJumpsAndDropsEvenWithADuplicateEndpoint(float height)
    {
        var target = new Vector3(0, height, 0);
        Assert.False(HeightPath.ValidShape([Vector3.Zero, target, target], Vector3.Zero, target));
    }

    [Fact]
    public void DoesNotSnapKnownNpcHeightToTheFloorBelow()
    {
        using var search = new HeightPath(Player, Poacher, 2.5f,
            (p, _, _) => p with { Y = Player.Y }, (_, _, _) => throw new Exception("Must not query the wrong floor"));
        Complete(search);
        Assert.Null(search.Result);
        Assert.Equal(0, search.Queries);
    }

    [Fact]
    public void MissingSurfaceDoesNotStartAQuery()
    {
        using var search = new HeightPath(Player, Poacher, 2.5f, (_, _, _) => null,
            (_, _, _) => throw new Exception("No mesh"));
        Complete(search);
        Assert.Null(search.Result);
    }

    [Fact]
    public void MissingCancelableIpcStopsWithoutARegularMoveFallback()
    {
        using var search = new HeightPath(Player, Poacher, 2.5f, (p, _, _) => p, (_, _, _) => null);
        Complete(search);
        Assert.Null(search.Result);
        Assert.Equal(1, search.Queries);
        Assert.Equal("path query IPC unavailable", search.LastFailure);
    }

    [Fact]
    public void TargetProjectionMustStayWithinInteractionRange()
    {
        using var search = new HeightPath(Player, Poacher, 1, (p, _, _) => p + new Vector3(2, 0, 0),
            (_, _, _) => throw new Exception("Projected endpoint is too far from NPC"));
        Complete(search);
        Assert.Null(search.Result);
    }

    [Fact]
    public void NpcApproachCanUseTheWholeInteractionRangeInsteadOfOnlyTwoMeters()
    {
        var target = new Vector3(100, 14, 0);
        var approach = target + new Vector3(2.33f, 0, 0);
        Vector3[] ramp = [Vector3.Zero, new(60, 0, 0), new(80, 14, 0), approach];
        var surface = Surface(ramp);
        using var search = new HeightPath(ramp[0], target, 2.5f, (p, xz, y) =>
            p == target ? xz >= 2.33f ? approach : null : surface(p, xz, y),
            (a, b, _) => Task.FromResult(a == ramp[0] ? ramp.ToList() : new List<Vector3> { a, b, b }));
        Complete(search);
        Assert.NotNull(search.Result);
        Assert.InRange(Vector3.Distance(search.Result[^1], target), 2.3f, 2.5f);
    }

    [Fact]
    public void ErodedStairEdgeUsesItsSupportedPointsInsteadOfTheSparseLineBesideIt()
    {
        var start = Vector3.Zero;
        Vector3[] ramp = [new(1, 0, 0), new(1, 0, 4), new(1, 4, 10)];
        var goal = ramp[^1];
        using var search = new HeightPath(start, goal, 2.5f, Surface(ramp),
            (_, _, _) => Task.FromResult(new List<Vector3> { start, goal, goal }));
        Complete(search);
        Assert.NotNull(search.Result);
        Assert.All(search.Result, p => Assert.Equal(1, p.X));
        Assert.True(HeightPath.ValidShape(search.Result, start, goal));
        Assert.Equal(1, search.Queries);
    }

    [Fact]
    public void IsolatedStartPolygonCanQueryANearbyOriginButMustTraceFromTheRealPlayer()
    {
        Vector3[] ramp = [Vector3.Zero, new(2, 0, 0), new(10, 4, 0)];
        var from = ramp[0]; var goal = ramp[^1];
        using var search = new HeightPath(from, goal, 2.5f, Surface(ramp), (a, b, _) =>
            Task.FromResult(a == from ? new List<Vector3> { a, b with { Y = b.Y - 4 }, b }
                : new List<Vector3> { a, b, b }));
        Complete(search);
        Assert.NotNull(search.Result);
        Assert.True(search.Queries > 1);
        Assert.True(HeightPath.ValidShape(search.Result, from, goal));
        Assert.InRange(Vector3.Distance(search.Result[0], from), 0, 1.5f);
    }

    [Fact]
    public void NearbyOriginOnAnUpperFloorCannotBypassTheUnsupportedConnector()
    {
        var goal = new Vector3(30, 4, 0);
        using var search = new HeightPath(Vector3.Zero, goal, 2.5f,
            (p, _, _) => p == goal ? p : p.X <= 1 ? p with { Y = 0 } : p with { Y = 4 },
            (a, b, _) => Task.FromResult(new List<Vector3> { a, b, b }));
        Complete(search);
        Assert.Null(search.Result);
    }

    [Fact]
    public void ValidRampIsAcceptedWithoutASideSearch()
    {
        Vector3[] ramp = [new(0, 0, 0), new(16, 0, 0), new(32, 14, 0), new(0, 14, 3)];
        using var search = new HeightPath(ramp[0], ramp[^1], 2.5f, Surface(ramp),
            (_, _, _) => Task.FromResult(ramp.Append(ramp[^1]).ToList()));
        Complete(search);
        Assert.NotNull(search.Result);
        Assert.Equal(1, search.Queries);
        Assert.Contains(ramp[2], search.Result);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OrdinaryTerrainMayChangeHeightBetweenHorizontalPathCorners(bool downhill)
    {
        Vector3[] ground = [new(0, 0, 0), new(60, 0, 0), new(80, 14, 0), new(100, 14, 0)];
        if (downhill) Array.Reverse(ground);
        var from = ground[0]; var goal = ground[^1];
        // vnavmesh string pulling omits the two middle height changes.
        using var search = new HeightPath(from, goal, 2.5f, Surface(ground),
            (_, _, _) => Task.FromResult(new List<Vector3> { from, goal, goal }));
        Complete(search);
        Assert.NotNull(search.Result);
        Assert.Equal(1, search.Queries);
    }

    [Fact]
    public void EndpointNeedNotBeDuplicatedAndPolygonCenterNeedNotEqualStart()
    {
        Vector3[] ground = [new(0, 0, 0), new(60, 0, 0), new(80, 14, 0), new(100, 14, 0)];
        using var search = new HeightPath(ground[0], ground[^1], 2.5f, Surface(ground),
            (a, b, _) => Task.FromResult(a == ground[0]
                ? new List<Vector3> { new(5, 0, 0), ground[2], ground[^1] }
                : new List<Vector3> { a, b, b }));
        Complete(search);
        Assert.NotNull(search.Result);
        Assert.InRange(search.Queries, 1, 2);
    }

    [Fact]
    public void ContinuousLowerFloorDoesNotValidateAGoalOnAnUnconnectedUpperFloor()
    {
        var goal = new Vector3(100, 14, 0);
        using var search = new HeightPath(Vector3.Zero, goal, 2.5f,
            (p, _, _) => p == goal ? p : p with { Y = 0 },
            (_, _, _) => Task.FromResult(new List<Vector3> { Vector3.Zero, goal, goal }));
        Complete(search);
        Assert.Null(search.Result);
    }

    [Fact]
    public void FindsAConnectedRampViaAnotherApproachInsteadOfTheFalseShortcut()
    {
        Vector3[] ramp = [new(0, 0, 0), new(16, 0, 0), new(32, 14, 0), new(4, 14, 3), new(0, 14, 3)];
        var from = ramp[0]; var goal = ramp[^1]; var via = ramp[^2];
        using var search = new HeightPath(from, goal, 2.5f, Surface(ramp), (a, b, _) =>
            Task.FromResult(a == from && b == via
                ? new List<Vector3> { from, ramp[1], ramp[2], via, via }
                : new List<Vector3> { a, b, b }));
        Complete(search);
        Assert.NotNull(search.Result);
        Assert.Equal(3, search.Queries);
        Assert.Contains(ramp[2], search.Result);
        Assert.Equal(goal, search.Result[^1]);
    }

    [Fact]
    public void GentleLookingDiagonalThroughEmptyAirIsRejected()
    {
        var goal = new Vector3(100, 14, 0);
        using var search = new HeightPath(Vector3.Zero, goal, 2.5f,
            (p, _, _) => p == goal ? p : null,
            (a, b, _) => Task.FromResult(new List<Vector3> { a, b, b }));
        Assert.True(HeightPath.ValidShape([Vector3.Zero, goal, goal], Vector3.Zero, goal));
        Complete(search);
        Assert.Null(search.Result);
    }

    [Fact]
    public void IncompleteSecondLegCannotBeUsed()
    {
        Vector3[] ramp = [new(0, 0, 0), new(16, 0, 0), new(32, 14, 0), new(4, 14, 3), new(0, 14, 3)];
        using var search = new HeightPath(ramp[0], ramp[^1], 2.5f, Surface(ramp), (a, b, _) =>
            Task.FromResult(a == ramp[0] && b == ramp[^2]
                ? new List<Vector3> { a, ramp[1], ramp[2], b, b }
                : new List<Vector3> { a, b with { Y = b.Y - 4 }, b }));
        Complete(search);
        Assert.Null(search.Result);
    }

    [Fact]
    public void ProbesAndQueriesAreBoundedAndNoCandidateCausesMovement()
    {
        var probes = 0;
        using var search = new HeightPath(Player, Poacher, 2.5f,
            (p, _, _) => { ++probes; return p; }, (a, b, _) => Task.FromResult(new List<Vector3> { a, a, b }));
        for (var i = 0; i < 1000 && !search.Done; ++i)
        {
            probes = 0;
            search.Update();
            Assert.InRange(probes, 0, 24);
        }
        Assert.True(search.Done);
        Assert.Null(search.Result);
        Assert.InRange(search.Queries, 1, 73);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidCoordinatesAreRejected(float y)
    {
        Assert.False(HeightPath.ValidShape([Player, new(0, y, 0), Poacher, Poacher], Player, Poacher));
    }

    [Fact]
    public void NormalWaypointConsumptionIsAllowedButNativeRouteReplacementIsNot()
    {
        List<Vector3> path = [Player, new(-130, 6, 40), new(-130, 18, 60), Poacher];
        Assert.True(HeightPath.IsRemainingPath(path, path.Skip(1).ToList()));
        Assert.True(HeightPath.IsRemainingPath(path, []));
        Assert.False(HeightPath.IsRemainingPath(path, [Player, Poacher, Poacher]));
        Assert.False(HeightPath.IsRemainingPath(path, [Poacher, Poacher, Poacher, Poacher]));
    }

    [Fact]
    public void StopCancelsSearchAndLateResultCannotResumeWalking()
    {
        var pending = new TaskCompletionSource<List<Vector3>>();
        CancellationToken token = default;
        using var search = new HeightPath(Player, Poacher, 2.5f, (p, _, _) => p,
            (_, _, ct) => { token = ct; return pending.Task; });
        search.Update();
        var walk = (AutoWalkService)RuntimeHelpers.GetUninitializedObject(typeof(AutoWalkService));
        var nav = (NavmeshIpc)RuntimeHelpers.GetUninitializedObject(typeof(NavmeshIpc));
        var stops = 0;
        Field(nav, "_stop", DestinationReadoutTests.StrictProxy.Of<ICallGateSubscriber<object>>(m =>
        { Assert.Equal("InvokeAction", m.Name); ++stops; return null; }));
        Field(walk, "_nav", nav);
        Field(walk, "_log", DestinationReadoutTests.StrictProxy.Of<IPluginLog>(_ => null));
        Field(walk, "_heightPath", search);
        Field(walk, "_phase", Enum.Parse(typeof(AutoWalkService).GetNestedType("Phase", BindingFlags.NonPublic)!, "HeightSearch"));
        Assert.True(walk.IsActive);
        walk.StopQuiet();
        Assert.True(token.IsCancellationRequested);
        Assert.False(walk.IsActive);
        Assert.Equal(1, stops);
        pending.SetResult([Player, Poacher, Poacher]);
        search.Update();
        Assert.Null(search.Result);
    }

    [Theory]
    [InlineData(true, 200f, false)] [InlineData(false, 200f, false)]
    [InlineData(true, 5f, false)] [InlineData(false, 5f, false)]
    [InlineData(true, 200f, true)] [InlineData(false, 5f, true)]
    public void NativeRetryAtFarOrNearDistanceStartsAnotherCheckInsteadOfEndingTheWalk(bool pending, float flatDistance, bool detourProgress)
    {
        var start = Vector3.Zero;
        var goal = new Vector3(flatDistance, 4, 0);
        var player = DestinationReadoutTests.StrictProxy.Of<IPlayerCharacter>(m => m.Name == "get_Position"
            ? start : throw new NotSupportedException(m.Name));
        var walk = (AutoWalkService)RuntimeHelpers.GetUninitializedObject(typeof(AutoWalkService));
        var nav = (NavmeshIpc)RuntimeHelpers.GetUninitializedObject(typeof(NavmeshIpc));
        var stops = 0;
        Field(nav, "_stop", DestinationReadoutTests.StrictProxy.Of<ICallGateSubscriber<object>>(m =>
        { Assert.Equal("InvokeAction", m.Name); ++stops; return null; }));
        Field(nav, "_isReady", DestinationReadoutTests.StrictProxy.Of<ICallGateSubscriber<bool>>(_ => true));
        Field(nav, "_pathfindInProgress", DestinationReadoutTests.StrictProxy.Of<ICallGateSubscriber<bool>>(_ => pending));
        Field(nav, "_listWaypoints", DestinationReadoutTests.StrictProxy.Of<ICallGateSubscriber<List<Vector3>>>(_ => new List<Vector3> { start, goal }));
        Field(walk, "_objectTable", DestinationReadoutTests.StrictProxy.Of<IObjectTable>(m => m.Name == "get_LocalPlayer"
            ? player : throw new NotSupportedException(m.Name)));
        Field(walk, "_clientState", DestinationReadoutTests.StrictProxy.Of<IClientState>(m => m.Name == "get_TerritoryType"
            ? 153u : throw new NotSupportedException(m.Name)));
        Field(walk, "_nav", nav);
        Field(walk, "_log", DestinationReadoutTests.StrictProxy.Of<IPluginLog>(_ => null));
        // Tolk is never initialized in this process: its native IsLoaded is false.
        Field(walk, "_tolk", RuntimeHelpers.GetUninitializedObject(typeof(TolkService)));
        Field(walk, "_checkedHeightRoute", new List<Vector3> { start, goal, goal });
        Field(walk, "_destPosition", goal);
        Field(walk, "_targetName", "Проверка маршрута");
        Field(walk, "_stopRange", 2.5f);
        Field(walk, "_startTerritory", (ushort)153);
        Field(walk, "_reengageBestDistance", float.MaxValue);
        if (detourProgress)
        {
            Field(walk, "_reengageCount", 1);
            Field(walk, "_reengageBestDistance", Vector3.Distance(start, goal) - 2);
            Field(walk, "_reengagePosition", new Vector3(-10, 0, 0));
        }
        Field(walk, "_phase", Enum.Parse(typeof(AutoWalkService).GetNestedType("Phase", BindingFlags.NonPublic)!, "Walking"));
        typeof(AutoWalkService).GetMethod("WalkingUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(walk, null);
        Assert.True(walk.IsActive);
        Assert.Equal(detourProgress ? 2 : 1, typeof(AutoWalkService).GetField("_reengageCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(walk));
        var search = (HeightPath)typeof(AutoWalkService).GetField("_heightPath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(walk)!;
        Assert.NotNull(search);
        Assert.Equal(start, search.Start);
        Assert.Equal(goal, search.Destination);
        Assert.Equal(0, search.Queries);
        Assert.Equal(2, stops);
        walk.StopQuiet();
        Assert.False(walk.IsActive);
        Assert.Null(search.Result);
    }

    [Fact]
    public void FaultedQueryIsObservedAndSearchFinishes()
    {
        using var search = new HeightPath(Player, Poacher, 2.5f, (p, _, _) => p,
            (_, _, _) => Task.FromException<List<Vector3>>(new InvalidOperationException("Mesh unloaded")));
        Complete(search);
        Assert.Null(search.Result);
    }

    [Theory]
    [InlineData(false, false, "HeightSearch", 0)]
    [InlineData(true, false, "HeightSearch", 0)]
    [InlineData(false, true, "HeightSearch", 0)]
    public void EveryGroundDestinationIsCheckedBeforeMovement(bool guessedHeight, bool transition, string expectedPhase, int expectedMoves)
    {
        var walk = (AutoWalkService)RuntimeHelpers.GetUninitializedObject(typeof(AutoWalkService));
        var nav = (NavmeshIpc)RuntimeHelpers.GetUninitializedObject(typeof(NavmeshIpc));
        var player = DestinationReadoutTests.StrictProxy.Of<IPlayerCharacter>(m => m.Name == "get_Position"
            ? Vector3.Zero : throw new NotSupportedException(m.Name));
        Field(walk, "_objectTable", DestinationReadoutTests.StrictProxy.Of<IObjectTable>(_ => player));
        Field(walk, "_clientState", DestinationReadoutTests.StrictProxy.Of<IClientState>(_ => 153u));
        Field(walk, "_log", DestinationReadoutTests.StrictProxy.Of<IPluginLog>(_ => null));
        Field(walk, "_tolk", RuntimeHelpers.GetUninitializedObject(typeof(TolkService)));
        Field(walk, "_nav", nav);
        Field(walk, "_destinationHeightIsGuess", guessedHeight);
        Field(walk, "_destinationIsTransition", transition);
        Field(nav, "_isReady", DestinationReadoutTests.StrictProxy.Of<ICallGateSubscriber<bool>>(_ => true));
        Field(nav, "_stop", DestinationReadoutTests.StrictProxy.Of<ICallGateSubscriber<object>>(_ => null));
        var moves = 0;
        Field(nav, "_moveCloseTo", DestinationReadoutTests.StrictProxy.Of<ICallGateSubscriber<Vector3, bool, float, bool>>(_ => { ++moves; return true; }));
        typeof(AutoWalkService).GetMethod("Begin", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(walk, [new Vector3(100, 14, 0), "Цель", 2.5f, 0UL, false]);
        Assert.Equal(expectedPhase, typeof(AutoWalkService).GetField("_phase", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(walk)!.ToString());
        Assert.Equal(expectedMoves, moves);
        walk.StopQuiet();
    }

    [Fact]
    public void LiveTargetResetsPreviousMapAndZoneTransitionFlags()
    {
        var walk = (AutoWalkService)RuntimeHelpers.GetUninitializedObject(typeof(AutoWalkService));
        var nav = (NavmeshIpc)RuntimeHelpers.GetUninitializedObject(typeof(NavmeshIpc));
        var player = DestinationReadoutTests.StrictProxy.Of<IPlayerCharacter>(_ => Vector3.Zero);
        Field(walk, "_objectTable", DestinationReadoutTests.StrictProxy.Of<IObjectTable>(_ => player));
        Field(walk, "_tolk", RuntimeHelpers.GetUninitializedObject(typeof(TolkService)));
        Field(walk, "_log", DestinationReadoutTests.StrictProxy.Of<IPluginLog>(_ => null));
        Field(walk, "_nav", nav);
        Field(walk, "_destinationHeightIsGuess", true);
        Field(walk, "_destinationIsTransition", true);
        Field(nav, "_isReady", DestinationReadoutTests.StrictProxy.Of<ICallGateSubscriber<bool>>(_ => false));
        Field(nav, "_stop", DestinationReadoutTests.StrictProxy.Of<ICallGateSubscriber<object>>(_ => null));
        Field(nav, "_buildProgress", DestinationReadoutTests.StrictProxy.Of<ICallGateSubscriber<float>>(_ => 0.5f));
        typeof(AutoWalkService).GetMethod("Begin", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(walk, [Poacher, "NPC", 2.5f, 123UL, false]);
        Assert.Equal(false, typeof(AutoWalkService).GetField("_destinationHeightIsGuess", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(walk));
        Assert.Equal(false, typeof(AutoWalkService).GetField("_destinationIsTransition", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(walk));
    }

    [Theory]
    [InlineData(1, false)] [InlineData(2, true)]
    public void CheckedRouteRetryDoesNotLoopWithoutProgressOrBeyondItsLimit(int attempts, bool moved)
    {
        var walk = (AutoWalkService)RuntimeHelpers.GetUninitializedObject(typeof(AutoWalkService));
        var player = DestinationReadoutTests.StrictProxy.Of<IPlayerCharacter>(_ => moved ? new Vector3(10, 0, 0) : Vector3.Zero);
        Field(walk, "_objectTable", DestinationReadoutTests.StrictProxy.Of<IObjectTable>(_ => player));
        Field(walk, "_log", DestinationReadoutTests.StrictProxy.Of<IPluginLog>(_ => null));
        Field(walk, "_reengageCount", attempts);
        Field(walk, "_reengageBestDistance", 100f);
        Field(walk, "_reengagePosition", Vector3.Zero);
        Field(walk, "_checkedHeightRoute", new List<Vector3>());
        Field(walk, "_stopRange", 2.5f);
        Assert.Equal(false, typeof(AutoWalkService).GetMethod("TryReengage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(walk, [101f]));
        Assert.Equal(attempts, typeof(AutoWalkService).GetField("_reengageCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(walk));
    }

    [Theory]
    [InlineData(LanguageMode.Russian, "выше", "ниже")]
    [InlineData(LanguageMode.English, "above", "below")]
    [InlineData(LanguageMode.German, "höher", "tiefer")]
    public void HeightReadoutUsesMetersAndDoesNotInventACompassDirection(LanguageMode mode, string up, string down)
    {
        var saved = Loc.Mode;
        try
        {
            Loc.Mode = mode;
            var text = AccessibilityStrings.WalkMeshEndsAtHeight(0.04f, "WRONG_DIRECTION", 13.943558f);
            Assert.Contains("14", text);
            Assert.Contains(up, text);
            Assert.DoesNotContain("WRONG_DIRECTION", text);
            Assert.Contains(down, AccessibilityStrings.TargetHeight(-14));
            Assert.Equal("", AccessibilityStrings.TargetHeight(0.5f));
            Assert.DoesNotContain("WRONG_DIRECTION", AccessibilityStrings.HeightPathUnavailable(14));
        }
        finally { Loc.Mode = saved; }
    }

    // A fixed model of two floors connected by a ramp, independent of the
    // planner's candidate generation and validation rules. No live mesh claim.
    private static Func<Vector3, float, float, Vector3?> Surface(Vector3[] polyline) => (p, xz, y) =>
    {
        Vector3? best = null;
        var distance = float.MaxValue;
        for (var i = 1; i < polyline.Length; ++i)
        {
            var a = polyline[i - 1]; var d = polyline[i] - a;
            // A point inside a mesh polygon keeps its X/Z; its Y is projected
            // onto the polygon's floor (ClosestPointOnPoly in vnavmesh).
            var flatLength = d.X * d.X + d.Z * d.Z;
            var t = flatLength == 0 ? 0 : Math.Clamp(((p.X - a.X) * d.X + (p.Z - a.Z) * d.Z) / flatLength, 0, 1);
            var q = a + d * t;
            var next = Vector3.Distance(p, q);
            if (GroundDetour.FlatDistance(p, q) <= xz && MathF.Abs(p.Y - q.Y) <= y && next < distance)
            { best = q; distance = next; }
        }
        return best;
    };

    private static void Complete(HeightPath search)
    {
        for (var i = 0; i < 2000 && !search.Done; ++i) search.Update();
        Assert.True(search.Done);
    }
    private static void Field(object instance, string field, object value) => instance.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
}
