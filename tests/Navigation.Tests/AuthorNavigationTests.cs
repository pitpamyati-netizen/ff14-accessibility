using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FF14Accessibility;
using FF14Accessibility.Services;

namespace Navigation.Tests;

// Exercise the copied author implementation through fake vnavmesh calls.
// No native game calls, movement or audio services are started by these tests.
public sealed class AuthorNavigationTests
{
    [Theory]
    [InlineData(0f)] [InlineData(15f)] [InlineData(-5f)]
    public void GroundWalkUsesNativeSearchAndMoveImmediately(float height)
    {
        var h = new Harness();
        var goal = new Vector3(20, height, 0);
        h.Walk.ToggleToPosition(goal, "Цель", 2.5f);
        Assert.Equal([(goal, false, 2.5f)], h.Requests);
        Assert.Equal(1, h.Stops);
        Assert.True(h.Walk.IsActive);
        Assert.Equal("Starting", h.Phase);
    }

    [Fact]
    public void SecondPositionWalkPressStopsInsteadOfStartingAnotherRoute()
    {
        var h = new Harness();
        h.Walk.ToggleToPosition(h.TargetPosition, "Цель", 2.5f);
        h.Walk.ToggleToPosition(new(30, 10, 0), "Другая цель", 2.5f);
        Assert.Single(h.Requests);
        Assert.Equal(2, h.Stops);
        Assert.False(h.Walk.IsActive);
        Assert.Equal("Guarding", h.Phase);
    }

    [Theory]
    [InlineData("Starting")] [InlineData("Walking")]
    [InlineData("TrailWalking")] [InlineData("Landing")]
    public void StoppingEveryAuthorWalkPhaseClearsTheNativePath(string phase)
    {
        var h = new Harness(phase) { Waypoints = [new(20, 0, 0)] };
        h.Walk.StopQuiet();
        Assert.Empty(h.Waypoints);
        Assert.Equal(1, h.Stops);
        Assert.Equal("Guarding", h.Phase);
        Assert.False(h.Walk.IsWalking);
    }

    [Fact]
    public void AuthorStopGuardClearsAPathDeliveredAfterStop()
    {
        var h = new Harness("Walking");
        h.Walk.StopQuiet();
        h.Waypoints = [new(20, 0, 0)];
        h.Invoke("GuardUpdate");
        Assert.Equal(2, h.Stops);
        Assert.Empty(h.Waypoints);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public void StopGuardWaitsForPendingNativeSearch()
    {
        var h = new Harness("Walking");
        h.Walk.StopQuiet();
        h.Set("_guardUntil", DateTime.UtcNow.AddSeconds(-1));
        h.NativeSearching = true;
        h.Invoke("GuardUpdate");
        Assert.Equal("Guarding", h.Phase);
        h.NativeSearching = false;
        h.Set("_guardUntil", DateTime.UtcNow.AddSeconds(-1));
        h.Invoke("GuardUpdate");
        Assert.Equal("Idle", h.Phase);
    }

    [Fact]
    public void FlightRequestsNativeAirRouteWithoutASeparateQuery()
    {
        var h = new Harness();
        h.Invoke("BeginFlightPath", [new Vector3(20, 10, 0), "Цель", 2.5f, 0UL]);
        Assert.Equal([(new Vector3(20, 10, 0), true, 2.5f)], h.Requests);
        Assert.True(h.Walk.IsActive);
        Assert.True((bool)h.Get("_flying")!);
    }

    [Fact]
    public void BusyFlightDoesNotStartACompetingGroundRoute()
    {
        var h = new Harness { NativeSearching = true, AcceptMove = _ => false };
        h.Invoke("BeginFlightPath", [new Vector3(20, 10, 0), "Цель", 2.5f, 0UL]);
        Assert.Single(h.Requests);
        Assert.True(h.Requests[0].Fly);
        Assert.False(h.Walk.IsActive);
        Assert.Equal("Guarding", h.Phase);
    }

    [Fact]
    public void UnavailableFlightFallsBackToAuthorsGroundWalk()
    {
        var h = new Harness { AcceptMove = fly => !fly };
        h.Invoke("BeginFlightPath", [new Vector3(20, 10, 0), "Цель", 2.5f, 0UL]);
        Assert.Equal(new[] { true, false }, h.Requests.Select(r => r.Fly));
        Assert.True(h.Walk.IsActive);
        Assert.False((bool)h.Get("_flying")!);
        Assert.Equal("Starting", h.Phase);
    }

    [Fact]
    public void NativeFlightSearchCanContinueBeyondGroundTimeout()
    {
        var h = new Harness("Starting") { NativeSearching = true };
        h.Set("_flying", true);
        h.Set("_startedAt", DateTime.UtcNow.AddSeconds(-10));
        h.Invoke("StartingUpdate");
        Assert.True(h.Walk.IsActive);
        Assert.Empty(h.Requests);
        Assert.Equal(0, h.Stops);
    }

    [Fact]
    public void FollowUsesTheSameNativeSearchAndMove()
    {
        var h = new Harness();
        h.Follow();
        h.Invoke("FollowUpdate");
        Assert.Equal([(h.TargetPosition, false, 3f)], h.Requests);
        Assert.True(h.Walk.IsFollowing);
    }

    [Fact]
    public void FollowRepathsWhenTheTargetMoves()
    {
        var h = new Harness();
        h.Follow();
        h.Invoke("FollowUpdate");
        h.TargetPosition = new(25, 0, 0);
        h.Set("_lastFollowPathAt", DateTime.UtcNow.AddSeconds(-1));
        h.Invoke("FollowUpdate");
        Assert.Equal(2, h.Requests.Count);
        Assert.Equal(h.TargetPosition, h.Requests[1].Position);
    }

    [Fact]
    public void FollowWaitsForNativeSearchRatherThanIssuingAnotherRequest()
    {
        var h = new Harness { NativeSearching = true };
        h.Follow();
        h.Invoke("FollowUpdate");
        Assert.Empty(h.Requests);
        Assert.True(h.Walk.IsFollowing);
    }

    [Fact]
    public void FollowWithinNativeStopRangeMakesNoNewRequest()
    {
        var h = new Harness { TargetPosition = new(3, 0, 0) };
        h.Follow();
        h.Invoke("FollowUpdate");
        Assert.Empty(h.Requests);
        Assert.True(h.Walk.IsFollowing);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void FollowStopsOnZoneChangeOrDisappearingTarget(bool changedZone)
    {
        var h = new Harness();
        h.Follow();
        if (changedZone) h.Territory = 134;
        else h.TargetPresent = false;
        h.Invoke("FollowUpdate");
        Assert.False(h.Walk.IsFollowing);
        Assert.Equal(1, h.Stops);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public void ManualFollowStopAlsoArmsTheLatePathGuard()
    {
        var h = new Harness();
        h.Follow();
        h.Walk.StopFollowQuiet();
        h.Waypoints = [new(20, 0, 0)];
        h.Invoke("GuardUpdate");
        Assert.False(h.Walk.IsFollowing);
        Assert.Equal(2, h.Stops);
        Assert.Empty(h.Waypoints);
    }

    private sealed class Harness
    {
        internal readonly AutoWalkService Walk = (AutoWalkService)RuntimeHelpers.GetUninitializedObject(typeof(AutoWalkService));
        private readonly NavmeshIpc _nav = (NavmeshIpc)RuntimeHelpers.GetUninitializedObject(typeof(NavmeshIpc));
        internal uint Territory = 133;
        internal Vector3 TargetPosition = new(20, 0, 0);
        internal bool TargetPresent = true, NativeSearching;
        internal List<Vector3> Waypoints = [];
        internal readonly List<(Vector3 Position, bool Fly, float Range)> Requests = [];
        internal Func<bool, bool> AcceptMove = _ => true;
        internal int Stops;
        internal string Phase => Get("_phase")!.ToString()!;

        internal Harness(string phase = "Idle")
        {
            var player = Proxy.Of<IPlayerCharacter>((m, _) => m.Name switch
            { "get_Position" => Vector3.Zero, "get_GameObjectId" => 1UL, "get_Address" => (nint)0,
                _ => throw new NotSupportedException(m.Name) });
            var target = Proxy.Of<IGameObject>((m, _) => m.Name switch
            { "get_Position" => TargetPosition, "get_GameObjectId" => 44UL,
                _ => throw new NotSupportedException(m.Name) });
            Set("_objectTable", Proxy.Of<IObjectTable>((m, _) => m.Name switch
            { "get_LocalPlayer" => player,
                "GetEnumerator" => (TargetPresent ? new List<IGameObject> { target } : []).GetEnumerator(),
                _ => throw new NotSupportedException(m.Name) }));
            Set("_clientState", Proxy.Of<IClientState>((m, _) => m.Name == "get_TerritoryType"
                ? Territory : throw new NotSupportedException(m.Name)));
            var log = Proxy.Of<IPluginLog>((_, _) => null);
            Set("_log", log);
            Set("_tolk", RuntimeHelpers.GetUninitializedObject(typeof(TolkService)));
            Set("_nav", _nav);
            Set("_config", new Configuration());
            Gate("_log", log);
            Gate("_stop", Proxy.Of<ICallGateSubscriber<object>>((_, _) => { ++Stops; Waypoints = []; return null; }));
            Gate("_isReady", Proxy.Of<ICallGateSubscriber<bool>>((_, _) => true));
            Gate("_isRunning", Proxy.Of<ICallGateSubscriber<bool>>((_, _) => Waypoints.Count > 0));
            Gate("_pathfindInProgress", Proxy.Of<ICallGateSubscriber<bool>>((_, _) => NativeSearching));
            Gate("_numWaypoints", Proxy.Of<ICallGateSubscriber<int>>((_, _) => Waypoints.Count));
            Gate("_listWaypoints", Proxy.Of<ICallGateSubscriber<List<Vector3>>>((_, _) => new List<Vector3>(Waypoints)));
            Gate("_nearestPoint", Proxy.Of<ICallGateSubscriber<Vector3, float, float, Vector3?>>((_, a) => (Vector3)a![0]!));
            Gate("_nearestPointReachable", Proxy.Of<ICallGateSubscriber<Vector3, float, float, Vector3?>>((_, a) => (Vector3)a![0]!));
            Gate("_moveCloseTo", Proxy.Of<ICallGateSubscriber<Vector3, bool, float, bool>>((_, a) =>
            {
                var request = ((Vector3)a![0]!, (bool)a[1]!, (float)a[2]!);
                Requests.Add(request);
                var accepted = AcceptMove(request.Item2);
                if (accepted) Waypoints = [request.Item1];
                return accepted;
            }));
            Set("_startTerritory", (ushort)133);
            Set("_destPosition", TargetPosition);
            Set("_targetName", "Цель");
            Set("_stopRange", 2.5f);
            Set("_usedTrails", new HashSet<string>());
            Set("_phase", Enum.Parse(typeof(AutoWalkService).GetNestedType("Phase", BindingFlags.NonPublic)!, phase));
        }

        internal void Follow()
        {
            Set("_following", true); Set("_followTargetId", 44UL);
            Set("_followStartTerritory", (ushort)133); Set("_followName", "Цель");
        }
        internal object? Invoke(string method, object?[]? args = null) => typeof(AutoWalkService)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Walk, args);
        internal void Set(string name, object value) => Field(Walk, name).SetValue(Walk, value);
        internal object? Get(string name) => Field(Walk, name).GetValue(Walk);
        private void Gate(string name, object value) => Field(_nav, name).SetValue(_nav, value);
        private static FieldInfo Field(object value, string name) => value.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
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
