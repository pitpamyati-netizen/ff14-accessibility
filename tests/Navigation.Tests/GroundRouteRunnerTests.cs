using System.Numerics;
using FF14Accessibility.Services;

namespace Navigation.Tests;

public class GroundRouteRunnerTests
{
    private sealed class Harness
    {
        internal List<Vector3> Remaining = [];
        internal int Moves, Jumps, Stops;
        internal bool Searching, Support = true, Clear = true, JumpAllowed = true;
        internal GroundRouteRunner Runner(List<GroundLeg> route) => new(route,
            points => { ++Moves; Remaining = points; return true; }, () => { ++Stops; Remaining = []; },
            () => new List<Vector3>(Remaining), () => Searching, (_, _) => Clear, (_, _) => JumpAllowed,
            _ => { ++Jumps; return true; }, _ => Support);
    }

    [Fact]
    public void JumpRequiresTakeoffRealFloorAndStableLanding()
    {
        var h = new Harness(); var landing = new Vector3(2.5f, 0.9f, 0);
        var runner = h.Runner([new(Vector3.Zero, [landing], true)]);
        var now = DateTime.UtcNow;
        runner.Update(Vector3.Zero, false, now); Assert.Equal(1, h.Jumps);
        h.Remaining.Clear();
        runner.Update(landing with { Y = 1.5f }, true, now.AddSeconds(0.4));
        runner.Update(landing with { Y = 0 }, false, now.AddSeconds(0.6)); Assert.False(runner.Done);
        h.Support = false;
        runner.Update(landing, false, now.AddSeconds(0.8)); Assert.False(runner.Done);
        h.Support = true;
        runner.Update(landing, false, now.AddSeconds(0.9)); Assert.False(runner.Done);
        runner.Update(landing, false, now.AddSeconds(1.1)); Assert.True(runner.Done); Assert.False(runner.Failed);
    }

    [Fact]
    public void NoTakeoffCannotBecomeSuccessfulJumpEvenAtTheDestination()
    {
        var h = new Harness(); var goal = new Vector3(2, 0, 0);
        var runner = h.Runner([new(Vector3.Zero, [goal], true)]);
        var now = DateTime.UtcNow; runner.Update(Vector3.Zero, false, now);
        runner.Update(goal, false, now.AddSeconds(1));
        Assert.True(runner.Failed); Assert.Contains("land", runner.Reason);
    }

    [Fact]
    public void ChangedLandingPreventsTheJumpAction()
    {
        var h = new Harness { JumpAllowed = false };
        var runner = h.Runner([new(Vector3.Zero, [new(2, 0, 0)], true)]);
        runner.Update(Vector3.Zero, false, DateTime.UtcNow);
        Assert.True(runner.Failed); Assert.Equal(0, h.Jumps); Assert.Equal(0, h.Moves);
    }

    [Fact]
    public void NativeReplacementDuringJumpKeepsTheOriginalJumpEdgeForRetryExclusion()
    {
        var h = new Harness(); var end = new Vector3(2.5f, 0.8f, 0);
        var runner = h.Runner([new(Vector3.Zero, [end], true)]);
        var now = DateTime.UtcNow; runner.Update(Vector3.Zero, false, now);
        h.Searching = true;
        runner.Update(new(1, 1.4f, 0), true, now.AddSeconds(0.3));
        Assert.True(runner.Failed); Assert.Equal(end, runner.Failure!.To); Assert.Equal(Vector3.Zero, runner.Failure.From);
        Assert.Equal(1, h.Jumps);
    }

    [Fact]
    public void NativeReplacementAfterLongWalkExcludesUpcomingEdgeRatherThanOldOrigin()
    {
        var h = new Harness();
        var runner = h.Runner([new(Vector3.Zero, [new(1, 0, 0), new(10, 0, 0), new(20, 0, 0)])]);
        var now = DateTime.UtcNow; runner.Update(Vector3.Zero, false, now);
        h.Remaining = [new(20, 0, 0)];
        runner.Update(new(10, 0, 0), false, now.AddSeconds(1));
        h.Remaining = [new(30, 0, 0)];
        runner.Update(new(11, 0, 0), false, now.AddSeconds(1.1));
        Assert.True(runner.Failed); Assert.True(runner.Failure!.To.X > 11);
    }

    [Fact]
    public void DisconnectedLegsAndInvalidPointsCannotMove()
    {
        var h = new Harness();
        var runner = h.Runner([new(Vector3.Zero, [new(1, 0, 0)]), new(new(10, 0, 0), [new(11, 0, 0)])]);
        runner.Update(Vector3.Zero, false, DateTime.UtcNow);
        Assert.True(runner.Failed); Assert.Equal(0, h.Moves);
        runner = h.Runner([new(Vector3.Zero, [new(float.NaN, 0, 0)])]);
        Assert.True(runner.Failed);
    }

    [Fact]
    public void StartPositionChangedCannotActivateTheOldRoute()
    {
        var h = new Harness(); var runner = h.Runner([new(Vector3.Zero, [new(20, 0, 0)])]);
        runner.Update(new(3, 0, 0), false, DateTime.UtcNow);
        Assert.True(runner.Failed); Assert.Equal(0, h.Moves);
    }

    [Fact]
    public void WalkingStopsBeforeNewCollisionAndDoesNotCountEmptyWaypointsAsArrival()
    {
        var h = new Harness(); var runner = h.Runner([new(Vector3.Zero, [new(20, 0, 0)])]);
        var now = DateTime.UtcNow; runner.Update(Vector3.Zero, false, now);
        h.Clear = false; runner.Update(new(1, 0, 0), false, now.AddSeconds(0.2));
        Assert.True(runner.Failed); Assert.Empty(h.Remaining);
        h.Clear = true; runner = h.Runner([new(Vector3.Zero, [new(20, 0, 0)])]);
        runner.Update(Vector3.Zero, false, now); h.Remaining.Clear();
        runner.Update(new(1, 0, 0), false, now.AddSeconds(0.2)); Assert.True(runner.Failed);
    }
}
