using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FF14Accessibility.Services;

namespace Navigation.Tests;

public class GroundDetourTests
{
    private static readonly Vector3 Start = new(58.22726f, 1.0359972f, 262.0598f);
    private static readonly Vector3 Corner = new(58.25f, 1.75f, 261.5f);
    private static readonly Vector3 End = new(59.5f, 1f, 259.25f);
    private static List<Vector3> Direct(Vector3 a, Vector3 b) => [a, b, b];

    [Fact]
    public void RecordedStallSelectsTheNextRealWaypointAndPreservesHeight()
    {
        Assert.True(GroundDetour.TryChoose(Start, [Corner, End, new(67, 1, 255)], out var corner, out var end));
        Assert.Equal(Corner, corner);
        Assert.Equal(End, end);
    }

    [Theory]
    [InlineData(1, 0.6f)] // level ground: not the reported uphill corner
    [InlineData(5, 0.6f)] // another floor or a cliff
    [InlineData(1.75f, 4)] // far waypoint, not a stalled corner
    [InlineData(1.75f, 0.1f)] // already inside the exclusion area
    public void DoesNotRecoverUnrelatedGeometry(float height, float flat)
    {
        Assert.False(GroundDetour.TryChoose(new(0, 1, 0), [new(0, height, flat), new(0, 1, 3), new(0, 1, 8)], out _, out _));
    }

    [Fact]
    public void AppendedDestinationAloneCannotProveAConnection()
    {
        Assert.False(GroundDetour.TryChoose(Start, [Corner, End], out _, out _));
        Assert.False(GroundDetour.ValidLeg([Start, Corner, End], Start, End, new(500, 0, 500)));
    }

    [Fact]
    public void RejectsTheOriginalPathThatCrossesTheStalledCorner()
    {
        Assert.False(GroundDetour.ValidLeg([Start, Corner, End, End], Start, End, Corner));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void RejectsWrongHeightAndInvalidCoordinates(float y)
    {
        Assert.False(GroundDetour.ValidLeg([new(0, y, 0), new(1, 0, 0), new(1, 0, 0)],
            Vector3.Zero, new(1, 0, 0), new(500, 0, 500)));
    }

    [Fact]
    public void FindsTwoMeshPathsThroughAGentlerSideApproach()
    {
        var calls = 0;
        using var plan = new GroundDetour(Start, Corner, End, p => p with { Y = 1.3f },
            (a, b, _) => { ++calls; return Task.FromResult(Direct(a, b)); });
        Complete(plan);
        Assert.NotNull(plan.Result);
        Assert.Equal(Start, plan.Result[0]);
        Assert.Equal(End, plan.Result[^1]);
        Assert.Equal(2, calls);
        for (var i = 1; i < plan.Result.Count; ++i)
            Assert.False(GroundDetour.SegmentNear(plan.Result[i-1], plan.Result[i], Corner));
    }

    [Fact]
    public void RejectsAPartialSecondLegEvenWhenItsAppendedPointIsTheGoal()
    {
        using var plan = new GroundDetour(Start, Corner, End, p => p,
            (a, b, _) => Task.FromResult(b == End ? new List<Vector3> { a, Corner, End } : Direct(a, b)));
        Complete(plan);
        Assert.Null(plan.Result);
    }

    [Fact]
    public void MissingMeshDoesNotInventAStraightRoute()
    {
        var calls = 0;
        using var plan = new GroundDetour(Start, Corner, End, _ => null,
            (a, b, _) => { ++calls; return Task.FromResult(Direct(a, b)); });
        Complete(plan);
        Assert.Null(plan.Result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void FailedQueriesEndWithoutAnEndlessRetry()
    {
        var calls = 0;
        using var plan = new GroundDetour(Start, Corner, End, p => p,
            (_, _, _) => { ++calls; return Task.FromException<List<Vector3>>(new Exception("mesh changed")); });
        Complete(plan);
        Assert.Null(plan.Result);
        Assert.InRange(calls, 1, 12);
    }

    [Fact]
    public void StopCancelsAnUnfinishedQueryAndIgnoresItsLateResult()
    {
        var pending = new TaskCompletionSource<List<Vector3>>();
        CancellationToken token = default;
        var calls = 0;
        using var plan = new GroundDetour(Start, Corner, End, p => p,
            (_, _, ct) => { ++calls; token = ct; return pending.Task; });
        plan.Update();
        Assert.False(plan.Done);
        plan.Dispose();
        Assert.True(token.IsCancellationRequested);
        pending.SetResult(Direct(Start, End));
        plan.Update();
        Assert.True(plan.Done);
        Assert.Null(plan.Result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void AutoWalkStopAlsoCancelsTheDetourAndStopsNativeMovement()
    {
        var pending = new TaskCompletionSource<List<Vector3>>();
        CancellationToken token = default;
        using var plan = new GroundDetour(Start, Corner, End, p => p,
            (_, _, ct) => { token = ct; return pending.Task; });
        plan.Update();
        var walk = (AutoWalkService)RuntimeHelpers.GetUninitializedObject(typeof(AutoWalkService));
        var nav = (NavmeshIpc)RuntimeHelpers.GetUninitializedObject(typeof(NavmeshIpc));
        var stops = 0;
        Set(nav, "_stop", DestinationReadoutTests.StrictProxy.Of<ICallGateSubscriber<object>>(m =>
        {
            Assert.Equal("InvokeAction", m.Name); ++stops; return null;
        }));
        Set(walk, "_nav", nav);
        Set(walk, "_log", DestinationReadoutTests.StrictProxy.Of<IPluginLog>(_ => null));
        Set(walk, "_groundDetour", plan);
        Set(walk, "_phase", Enum.Parse(typeof(AutoWalkService).GetNestedType("Phase", BindingFlags.NonPublic)!, "DetourSearch"));
        Assert.True(walk.IsActive);
        walk.StopQuiet();
        Assert.True(token.IsCancellationRequested);
        Assert.False(walk.IsActive);
        Assert.Equal(1, stops);
        pending.SetResult(Direct(Start, End));
        plan.Update();
        Assert.Null(plan.Result);
    }

    private static void Complete(GroundDetour plan)
    {
        for (var i = 0; i < 100 && !plan.Done; ++i) plan.Update();
        Assert.True(plan.Done);
    }
    private static void Set(object value, string field, object data) => value.GetType()
        .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(value, data);
}
