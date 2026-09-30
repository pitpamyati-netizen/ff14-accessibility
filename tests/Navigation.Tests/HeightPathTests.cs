using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.Types;
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
                : new List<Vector3> { a, a, b }));
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
        Assert.InRange(search.Queries, 1, 49);
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

    [Fact]
    public void FaultedQueryIsObservedAndSearchFinishes()
    {
        using var search = new HeightPath(Player, Poacher, 2.5f, (p, _, _) => p,
            (_, _, _) => Task.FromException<List<Vector3>>(new InvalidOperationException("Mesh unloaded")));
        Complete(search);
        Assert.Null(search.Result);
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
            var t = Math.Clamp(Vector3.Dot(p - a, d) / d.LengthSquared(), 0, 1);
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
