using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FF14Accessibility.Services;
using FF14Accessibility;

namespace Navigation.Tests;

public sealed class NavigationLifecycleTests
{
    [Theory]
    [InlineData("HeightSearch")]
    [InlineData("Starting")]
    [InlineData("Walking")]
    [InlineData("DetourSearch")]
    [InlineData("DetourWalking")]
    [InlineData("TrailWalking")]
    [InlineData("Landing")]
    public void ChangingZoneStopsEveryMovementPhase(string phase)
    {
        var h = new Harness(phase) { Territory = 134 };
        h.Tick(phase);
        Assert.False(h.Walk.IsActive);
        Assert.True(h.Stops > 0);
        Assert.Equal(0, h.Moves);
    }

    [Theory]
    [InlineData("HeightSearch")]
    [InlineData("Starting")]
    [InlineData("Walking")]
    [InlineData("DetourSearch")]
    [InlineData("DetourWalking")]
    [InlineData("TrailWalking")]
    public void MissingLiveTargetDoesNotWalkToItsLastKnownPosition(string phase)
    {
        var h = new Harness(phase);
        h.Set("_targetId", 44UL);
        h.TargetPresent = false;
        h.Tick(phase);
        Assert.False(h.Walk.IsActive);
        Assert.True(h.Stops > 0);
        Assert.Equal(0, h.Moves);
    }

    [Theory]
    [InlineData("DetourWalking")]
    [InlineData("TrailWalking")]
    public void ForeignPathWithSameCountIsRejected(string phase)
    {
        var h = new Harness(phase);
        h.Set("_checkedAuxiliaryRoute", new List<Vector3> { Vector3.Zero, new(20, 0, 0) });
        h.Waypoints = [Vector3.Zero, new(30, 0, 0)];
        h.Tick(phase);
        Assert.False(h.Walk.IsActive);
        Assert.True(h.Stops > 0);
    }

    [Fact]
    public void EmptyTrailFarFromItsEndpointDoesNotAnnounceSuccessfulCrossing()
    {
        var h = new Harness("TrailWalking");
        h.Set("_checkedAuxiliaryRoute", new List<Vector3> { Vector3.Zero, new(20, 0, 0) });
        h.Set("_trailEnd", new Vector3(20, 0, 0));
        h.Set("_trailLength", 20f);
        h.Set("_trailStartedAt", DateTime.UtcNow.AddSeconds(-2));
        h.Tick("TrailWalking");
        Assert.False(h.Walk.IsActive);
        Assert.Null(h.Get("_heightPath"));
    }

    [Fact]
    public void OldRouteIsClearedEvenIfNewTargetIsAlreadyInRange()
    {
        var h = new Harness("Walking");
        h.Begin(new Vector3(1, 0, 0));
        Assert.False(h.Walk.IsActive);
        Assert.True(h.Stops > 0);
        Assert.Equal(0, h.Moves);
    }

    [Fact]
    public void CancellingFlightSearchCancelsTokenAndDiscardsLateCompletion()
    {
        var h = new Harness("Starting");
        var completion = new TaskCompletionSource<List<Vector3>>();
        var cancel = new CancellationTokenSource();
        h.Set("_flightPath", completion.Task);
        h.Set("_flightPathCancel", cancel);
        h.Walk.StopQuiet();
        Assert.True(cancel.IsCancellationRequested);
        completion.SetResult([Vector3.Zero, new(20, 5, 0), new(20, 5, 0)]);
        Assert.Null(h.Get("_flightPath"));
        Assert.Equal(0, h.Moves);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FlightPathChecksRealVoxelBeforeTheAppendedGoal(bool partial)
    {
        var goal = new Vector3(100, 10, 0);
        Assert.Equal(!partial, AutoWalkService.ValidFlightPath(
            [Vector3.Zero, partial ? new(40, 10, 0) : new(99, 10, 0), goal], goal, 2.5f));
    }

    [Fact]
    public void FlatPartialRouteCannotBeCertifiedFromTheOtherMeshIsland()
    {
        using var path = new HeightPath(Vector3.Zero, new(20, 0, 0), 2.5f, (p, _, _) => p,
            (a, b, _) => Task.FromResult(new List<Vector3> { a, a, b }));
        Complete(path);
        Assert.Null(path.Result);
        Assert.Contains("endpoint", path.LastFailure);
    }

    [Fact]
    public void NearbyMeshPointsDoNotPermitWalkingThroughAWall()
    {
        using var path = new HeightPath(Vector3.Zero, new(20, 0, 0), 2.5f, (p, _, _) => p,
            (a, b, _) => Task.FromResult(new List<Vector3> { a, b, b }),
            (a, b) => (a.X < 10) == (b.X < 10));
        Complete(path);
        Assert.Null(path.Result);
    }

    [Fact]
    public void LongOrdinaryRouteIsNotRejectedAtTwoKilometersOr1024SurfaceSamples()
    {
        using var path = new HeightPath(Vector3.Zero, new(3000, 0, 0), 2.5f, (p, _, _) => p,
            (a, b, _) => Task.FromResult(new List<Vector3> { a, b, b }));
        Complete(path);
        Assert.NotNull(path.Result);
        Assert.True(path.Result.Count > 1024);
    }

    [Fact]
    public void ShortRouteMayProduceOnlyOneSupportedPoint()
    {
        using var path = new HeightPath(Vector3.Zero, new(0.02f, 0, 0), 0.1f, (p, _, _) => p,
            (a, b, _) => Task.FromResult(new List<Vector3> { a, b, b }));
        Complete(path);
        Assert.Single(path.Result!);
    }

    [Fact]
    public void WalkableSlopeNearNavmeshs55DegreeLimitIsNotMistakenForAFloorJump()
    {
        var goal = new Vector3(10, 14, 0);
        using var path = new HeightPath(Vector3.Zero, goal, 2.5f,
            (p, _, _) => p with { Y = p.X * 1.4f },
            (a, b, _) => Task.FromResult(new List<Vector3> { a, b, b }));
        Complete(path);
        Assert.NotNull(path.Result);
    }

    [Fact]
    public void TransitionCanApproachMeshEdgeWithoutRequiringMeshInsideTrigger()
    {
        var trigger = new Vector3(20, 0, 0);
        using var path = new HeightPath(Vector3.Zero, trigger, 0.2f,
            (p, _, _) => p.X >= 15 ? new Vector3(15, 0, p.Z) : p,
            (a, b, _) => Task.FromResult(new List<Vector3> { a, b, b }), approachRange: 6);
        Complete(path);
        Assert.NotNull(path.Result);
        Assert.Equal(new Vector3(15, 0, 0), path.Result[^1]);
    }

    [Fact]
    public void NativeRetryAtAnUphillCornerCanStillUseTheLocalDetour()
    {
        var h = new Harness("Walking");
        h.Set("_checkedHeightRoute", new List<Vector3> { new(0, 0.6f, 0.8f), new(0, 0, 3), new(20, 0, 0) });
        h.Set("_lastGroundWaypoints", new List<Vector3> { new(0, 0.6f, 0.8f), new(0, 0, 3), new(20, 0, 0) });
        h.Waypoints = [new(0, 0, 1), new(20, 0, 0)];
        h.Tick("Walking");
        Assert.NotNull(h.Get("_groundDetour"));
        Assert.Null(h.Get("_heightPath"));
        Assert.True(h.Walk.IsActive);
        h.Walk.StopQuiet();
    }

    [Theory]
    [InlineData(2)] [InlineData(4)]
    public void BrokenRecordedPointsAreRejectedBeforeTheyReachMovement(int dimensions)
        => Assert.False(TrailService.ValidPoints(new NavTrail { Points = [new float[dimensions], [1, 0, 0]] }));

    [Fact]
    public void NonFiniteRecordedPointsAreRejectedBeforeMovement()
        => Assert.False(TrailService.ValidPoints(new NavTrail { Points = [[float.NaN, 0, 0], [1, 0, 0]] }));

    [Fact]
    public void DynamicWallStopsAnAlreadyValidatedRoute()
    {
        var h = new Harness("Walking");
        h.Set("_groundSegmentClear", new Func<Vector3, Vector3, bool>((_, _) => false));
        h.Waypoints = [new(10, 0, 0), new(20, 0, 0)];
        h.Tick("Walking");
        Assert.False(h.Walk.IsActive);
        Assert.True(h.Stops > 0);
    }

    [Fact]
    public void FollowingUsesQueryOnlyAndStopsWhenTargetReturnsWithinRange()
    {
        var h = new Harness();
        h.Follow();
        h.Tick("Follow");
        Assert.Equal(0, h.Moves);
        for (var i = 0; i < 10 && h.Moves == 0; ++i) h.Tick("Follow");
        Assert.Equal(1, h.Moves);
        h.TargetPosition = new(1, 0, 0);
        h.Tick("Follow");
        Assert.True(h.Walk.IsFollowing);
        Assert.Empty(h.Waypoints);
        Assert.Null(h.Get("_followRoute"));
    }

    [Fact]
    public void BlockedFollowingStopsInsteadOfRepeatingAnIdenticalPathForever()
    {
        var h = new Harness();
        h.Follow();
        h.Set("_followLastMoveAt", DateTime.UtcNow.AddSeconds(-5));
        h.Tick("Follow");
        Assert.False(h.Walk.IsFollowing);
        Assert.Equal(0, h.Moves);
    }

    [Fact]
    public void FollowingHasTimeToStartMovingAfterAComputationLongerThanTheStallLimit()
    {
        var h = new Harness();
        h.Follow();
        h.Tick("Follow");
        h.Set("_followLastMoveAt", DateTime.UtcNow.AddSeconds(-10));
        for (var i = 0; i < 10 && h.Moves == 0; ++i) h.Tick("Follow");
        Assert.Equal(1, h.Moves);
        h.Tick("Follow");
        Assert.True(h.Walk.IsFollowing);
    }

    [Fact]
    public void ANewFollowSearchGetsItsOwnDeadlineAfterACompletedLongWalk()
    {
        var h = new Harness();
        h.Follow();
        h.Set("_followSearchStartedAt", DateTime.UtcNow.AddSeconds(-60));
        h.Tick("Follow");
        h.Tick("Follow");
        Assert.True(h.Walk.IsFollowing);
    }

    [Fact]
    public void ContinuallyChangingFollowTargetCannotExtendOneSearchWithoutLimit()
    {
        var h = new Harness();
        h.Follow();
        h.Tick("Follow");
        h.Set("_followSearchStartedAt", DateTime.UtcNow.AddSeconds(-31));
        h.TargetPosition = new(30, 0, 0);
        h.Tick("Follow");
        Assert.False(h.Walk.IsFollowing);
        Assert.Equal(0, h.Moves);
    }

    [Fact]
    public void FollowCancellationDiscardsPendingQuery()
    {
        var h = new Harness();
        h.Follow();
        var completion = new TaskCompletionSource<List<Vector3>>();
        CancellationToken token = default;
        h.Query = (_, _, ct) => { token = ct; return completion.Task; };
        h.Tick("Follow");
        h.Tick("Follow");
        h.Walk.StopFollowQuiet();
        Assert.True(token.IsCancellationRequested);
        completion.SetResult([Vector3.Zero, h.TargetPosition, h.TargetPosition]);
        Assert.False(h.Walk.IsFollowing);
        Assert.Equal(0, h.Moves);
    }

    private static void Complete(HeightPath path)
    {
        for (var i = 0; i < 2000 && !path.Done; ++i) path.Update();
        Assert.True(path.Done);
    }

    private sealed class Harness
    {
        internal readonly AutoWalkService Walk = (AutoWalkService)RuntimeHelpers.GetUninitializedObject(typeof(AutoWalkService));
        private readonly NavmeshIpc _nav = (NavmeshIpc)RuntimeHelpers.GetUninitializedObject(typeof(NavmeshIpc));
        internal uint Territory = 133;
        internal Vector3 TargetPosition = new(20, 0, 0);
        internal bool TargetPresent = true;
        internal List<Vector3> Waypoints = [];
        internal int Stops, Moves;
        internal Func<Vector3, Vector3, CancellationToken, Task<List<Vector3>>> Query
            = (a, b, _) => Task.FromResult(new List<Vector3> { a, b, b });

        internal Harness(string phase = "Idle")
        {
            var player = Proxy.Of<IPlayerCharacter>((m, _) => m.Name switch
            { "get_Position" => Vector3.Zero, "get_GameObjectId" => 1UL, "get_Address" => (nint)0,
                _ => throw new NotSupportedException(m.Name) });
            var target = Proxy.Of<IGameObject>((m, _) => m.Name switch
            { "get_Position" => TargetPosition, "get_GameObjectId" => 44UL, _ => throw new NotSupportedException(m.Name) });
            Set("_objectTable", Proxy.Of<IObjectTable>((m, _) => m.Name switch
            { "get_LocalPlayer" => player, "GetEnumerator" => (TargetPresent ? new List<IGameObject> { target } : []).GetEnumerator(),
                _ => throw new NotSupportedException(m.Name) }));
            Set("_clientState", Proxy.Of<IClientState>((_, _) => Territory));
            Set("_log", Proxy.Of<IPluginLog>((_, _) => null));
            Set("_tolk", RuntimeHelpers.GetUninitializedObject(typeof(TolkService)));
            Set("_nav", _nav);
            Gate("_log", Proxy.Of<IPluginLog>((_, _) => null));
            Gate("_stop", Proxy.Of<ICallGateSubscriber<object>>((_, _) => { ++Stops; Waypoints = []; return null; }));
            Gate("_isReady", Proxy.Of<ICallGateSubscriber<bool>>((_, _) => true));
            Gate("_isRunning", Proxy.Of<ICallGateSubscriber<bool>>((_, _) => Waypoints.Count > 0));
            Gate("_pathfindInProgress", Proxy.Of<ICallGateSubscriber<bool>>((_, _) => false));
            Gate("_listWaypoints", Proxy.Of<ICallGateSubscriber<List<Vector3>>>((_, _) => new List<Vector3>(Waypoints)));
            Gate("_nearestPoint", Proxy.Of<ICallGateSubscriber<Vector3, float, float, Vector3?>>((_, a) => (Vector3)a![0]!));
            Gate("_findPath", Proxy.Of<ICallGateSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>>>(
                (_, a) => Query((Vector3)a![0]!, (Vector3)a[1]!, (CancellationToken)a[3]!)));
            Gate("_moveAlong", Proxy.Of<ICallGateSubscriber<List<Vector3>, bool, object>>((_, a) =>
            { ++Moves; Waypoints = new List<Vector3>((List<Vector3>)a![0]!); return null; }));
            Set("_startTerritory", (ushort)133);
            Set("_destPosition", TargetPosition);
            Set("_plannedDestination", TargetPosition);
            Set("_targetName", "Цель");
            Set("_stopRange", 2.5f);
            Set("_detourAttempts", new List<Vector3>());
            Set("_usedTrails", new HashSet<string>());
            Set("_phase", Enum.Parse(typeof(AutoWalkService).GetNestedType("Phase", BindingFlags.NonPublic)!, phase));
        }
        internal void Follow()
        {
            Set("_following", true); Set("_followTargetId", 44UL);
            Set("_followStartTerritory", (ushort)133); Set("_followName", "Цель");
            Set("_followLastMoveAt", DateTime.UtcNow); Set("_followSearchStartedAt", DateTime.UtcNow);
        }
        internal void Begin(Vector3 destination) => Invoke("Begin", [destination, "Цель", 2.5f, 0UL, false]);
        internal void Tick(string phase) => Invoke(phase switch
        { "Follow" => "FollowUpdate", "HeightSearch" => "HeightSearchUpdate", "Starting" => "StartingUpdate",
            "Landing" => "LandingUpdate", _ => phase + "Update" }, null);
        private object? Invoke(string method, object?[]? args) => typeof(AutoWalkService)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Walk, args);
        internal void Set(string name, object value) => Field(Walk, name).SetValue(Walk, value);
        internal object? Get(string name) => Field(Walk, name).GetValue(Walk);
        private void Gate(string name, object value) => Field(_nav, name).SetValue(_nav, value);
        private static FieldInfo Field(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    }

    public class Proxy : DispatchProxy
    {
        private Func<MethodInfo, object?[]?, object?> _call = null!;
        internal static T Of<T>(Func<MethodInfo, object?[]?, object?> call) where T : class
        {
            var value = Create<T, Proxy>();
            ((Proxy)(object)value)._call = call;
            return value;
        }
        protected override object? Invoke(MethodInfo? method, object?[]? args) => _call(method!, args);
    }
}
