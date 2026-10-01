using System.Numerics;
using FF14Accessibility.Services;
using Harness = Navigation.Tests.NavigationLifecycleTests.Harness;

namespace Navigation.Tests;

public class AdaptiveNavigationLifecycleTests
{
    [Theory]
    [InlineData("AdaptiveSearch")] [InlineData("AdaptiveWalking")]
    public void ZoneChangeStopsEveryNewPhase(string phase)
    {
        var h = new Harness(phase) { Territory = 134 };
        h.Tick(phase);
        Assert.False(h.Walk.IsActive); Assert.True(h.Stops > 0); Assert.Equal(0, h.Moves);
    }

    [Theory]
    [InlineData("AdaptiveSearch")] [InlineData("AdaptiveWalking")]
    public void LostTargetStopsEveryNewPhase(string phase)
    {
        var h = new Harness(phase) { TargetPresent = false };
        h.Set("_targetId", 44UL); h.Tick(phase);
        Assert.False(h.Walk.IsActive); Assert.True(h.Stops > 0); Assert.Equal(0, h.Moves);
    }

    [Fact]
    public void UserCancellationCancelsAdaptiveSearchAndDiscardsLateResult()
    {
        var h = new Harness("AdaptiveSearch");
        var completion = new TaskCompletionSource<List<Vector3>>(); CancellationToken token = default;
        var plan = new AdaptiveGroundPath(Vector3.Zero, h.TargetPosition, 2.5f, (p, _, _) => p,
            (_, _, ct) => { token = ct; return completion.Task; }, (_, _) => true);
        h.Set("_adaptiveGround", plan); h.Set("_groundRepairStartedAt", DateTime.UtcNow);
        h.Tick("AdaptiveSearch"); h.Tick("AdaptiveSearch");
        h.Walk.StopQuiet(); Assert.True(token.IsCancellationRequested);
        completion.SetResult([Vector3.Zero, h.TargetPosition, h.TargetPosition]);
        Assert.Null(h.Get("_adaptiveGround")); Assert.Null(h.Get("_groundRunner")); Assert.Equal(0, h.Moves);
    }

    [Fact]
    public void ChangedTargetCancelsOldAdaptiveSearchBeforeAnyMovement()
    {
        var h = new Harness("AdaptiveSearch"); h.Set("_targetId", 44UL);
        var plan = new AdaptiveGroundPath(Vector3.Zero, h.TargetPosition, 2.5f, (p, _, _) => p,
            (a, b, _) => Task.FromResult(new List<Vector3> { a, b, b }), (_, _) => true);
        h.Set("_adaptiveGround", plan); h.TargetPosition = new(30, 0, 0);
        h.Tick("AdaptiveSearch");
        Assert.True(plan.Done); Assert.Null(h.Get("_adaptiveGround")); Assert.NotNull(h.Get("_heightPath")); Assert.Equal(0, h.Moves);
    }

    [Fact]
    public void RetryBudgetStopsAnImpossibleRouteInsteadOfRestartingForever()
    {
        var h = new Harness("Walking"); h.Set("_groundRepairs", 8);
        h.Set("_groundSegmentClear", new Func<Vector3, Vector3, bool>((_, _) => false));
        h.Waypoints = [new(20, 0, 0)]; h.Tick("Walking");
        Assert.False(h.Walk.IsActive); Assert.Null(h.Get("_adaptiveGround")); Assert.Empty(h.Waypoints);
    }

    [Fact]
    public void CloseFollowTargetBehindWallDoesNotCancelItsRecovery()
    {
        var h = new Harness { TargetPosition = new(2, 0, 0) }; h.Follow();
        h.Set("_groundSegmentClear", new Func<Vector3, Vector3, bool>((_, _) => false));
        h.Tick("Follow");
        Assert.NotNull(h.Get("_followGroundPath")); Assert.True(h.Walk.IsFollowing); Assert.Equal(0, h.Moves);
    }

    [Fact]
    public void FollowCancellationCancelsAdaptiveSearchAsWellAsOrdinarySearch()
    {
        var h = new Harness(); h.Follow();
        var completion = new TaskCompletionSource<List<Vector3>>(); CancellationToken token = default;
        var plan = new AdaptiveGroundPath(Vector3.Zero, h.TargetPosition, 3, (p, _, _) => p,
            (_, _, ct) => { token = ct; return completion.Task; }, (_, _) => true);
        h.Set("_followAdaptivePath", plan); h.Set("_followSearchStart", Vector3.Zero);
        h.Set("_lastFollowDest", h.TargetPosition);
        h.Tick("Follow"); h.Tick("Follow"); h.Walk.StopFollowQuiet();
        Assert.True(token.IsCancellationRequested); Assert.Null(h.Get("_followAdaptivePath")); Assert.Equal(0, h.Moves);
    }

    [Fact]
    public void FailedJumpWaitsForLandingBeforeAnotherSearch()
    {
        var h = new Harness("AdaptiveWalking");
        h.Set("_targetId", 44UL); h.TargetPosition = new(30, 0, 0);
        h.Set("_groundSegmentClear", new Func<Vector3, Vector3, bool>((_, _) => true));
        h.Set("_groundJumping", new Func<bool>(() => true));
        h.Set("_groundLandingSupport", new Func<Vector3, bool>(_ => false));
        var runner = new GroundRouteRunner([new(Vector3.Zero, [new(2.5f, 0.8f, 0)], true)],
            _ => true, () => { }, () => [], () => true, (_, _) => true, (_, _) => true, _ => true, _ => false);
        var now = DateTime.UtcNow; runner.Update(Vector3.Zero, false, now);
        runner.Update(new(1, 1.4f, 0), true, now.AddSeconds(0.2));
        h.Set("_groundRunner", runner); h.PlayerPosition = new(1, 1.4f, 0);
        h.Tick("AdaptiveWalking"); h.Set("_groundFailedJumpAt", DateTime.UtcNow.AddSeconds(-1));
        h.Tick("AdaptiveWalking"); Assert.Null(h.Get("_adaptiveGround")); Assert.True(h.Walk.IsActive);
        h.Set("_groundJumping", new Func<bool>(() => false));
        h.Set("_groundLandingSupport", new Func<Vector3, bool>(_ => true)); h.PlayerPosition = new(1, 0, 0);
        h.Tick("AdaptiveWalking"); Assert.NotNull(h.Get("_adaptiveGround"));
        h.Tick("AdaptiveSearch"); Assert.NotNull(h.Get("_adaptiveGround")); Assert.Null(h.Get("_heightPath"));
    }

    [Fact]
    public void SuccessfulFullFollowRecoveryRenewsItsRetryBudget()
    {
        var h = new Harness { PlayerPosition = new(10, 0, 0) }; h.Follow();
        h.Set("_lastFollowDest", h.TargetPosition); h.Set("_followRepairs", 8);
        var end = h.PlayerPosition;
        var runner = new GroundRouteRunner([new(end, [end])], _ => true, () => { }, () => [], () => false,
            (_, _) => true, (_, _) => false, _ => false, _ => true);
        runner.Update(end, false, DateTime.UtcNow); runner.Update(end, false, DateTime.UtcNow.AddSeconds(0.1));
        h.Set("_followRunner", runner); h.Tick("Follow");
        Assert.Equal(0, h.Get("_followRepairs")); Assert.True(h.Walk.IsFollowing);
    }

    [Fact]
    public void SafeCloseFollowRenewsBudgetBeforeClearingTheStillRunningRoute()
    {
        var h = new Harness(); h.Follow(); h.Set("_followRepairs", 8);
        h.Set("_groundSegmentClear", new Func<Vector3, Vector3, bool>((_, _) => true));
        h.Set("_followFailures", new List<GroundFailure> { new(Vector3.Zero, new(1, 0, 0), false) });
        h.Set("_followApproaches", new List<Vector3> { new(10, 0, 0) });
        h.Waypoints = [new(20, 0, 0)]; h.PlayerPosition = new(18, 0, 0);
        h.Tick("Follow");
        Assert.Equal(0, h.Get("_followRepairs")); Assert.Empty((List<Vector3>)h.Get("_followApproaches")!);
        Assert.Empty((List<GroundFailure>)h.Get("_followFailures")!); Assert.Empty(h.Waypoints);
    }

    [Fact]
    public void FollowTargetMovementCannotSkipWaitingForFailedJumpLanding()
    {
        var h = new Harness(); h.Follow(); h.Set("_lastFollowDest", h.TargetPosition);
        h.Set("_groundSegmentClear", new Func<Vector3, Vector3, bool>((_, _) => true));
        h.Set("_groundJumping", new Func<bool>(() => true));
        h.Set("_groundLandingSupport", new Func<Vector3, bool>(_ => false));
        var runner = new GroundRouteRunner([new(Vector3.Zero, [new(2.5f, 0.8f, 0)], true)],
            _ => true, () => { }, () => [], () => true, (_, _) => true, (_, _) => true, _ => true, _ => false);
        var now = DateTime.UtcNow; runner.Update(Vector3.Zero, false, now);
        runner.Update(new(1, 1.4f, 0), true, now.AddSeconds(0.2));
        h.Set("_followRunner", runner); h.PlayerPosition = new(1, 1.4f, 0); h.TargetPosition = new(30, 0, 0);
        h.Tick("Follow");
        Assert.Same(runner, h.Get("_followRunner")); Assert.Null(h.Get("_followAdaptivePath")); Assert.Null(h.Get("_followGroundPath"));
        Assert.True(h.Walk.IsFollowing);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void StalledExternalQueryCannotExtendAdaptiveDeadlineForever(bool follow)
    {
        var h = new Harness(follow ? "Idle" : "AdaptiveSearch") { NativeSearching = true };
        var plan = new AdaptiveGroundPath(Vector3.Zero, h.TargetPosition, 2.5f, (p, _, _) => p,
            (a, b, _) => Task.FromResult(new List<Vector3> { a, b, b }), (_, _) => true);
        if (follow)
        {
            h.Follow(); h.Set("_followAdaptivePath", plan); h.Set("_lastFollowDest", h.TargetPosition);
            h.Set("_followSearchStart", Vector3.Zero); h.Set("_followSearchStartedAt", DateTime.UtcNow.AddSeconds(-31));
            h.Tick("Follow"); Assert.False(h.Walk.IsFollowing);
        }
        else
        {
            h.Set("_adaptiveGround", plan); h.Set("_groundRepairStartedAt", DateTime.UtcNow.AddSeconds(-31));
            h.Tick("AdaptiveSearch"); Assert.False(h.Walk.IsActive);
        }
        Assert.True(plan.Done); Assert.True(h.Stops > 0); Assert.Equal(0, h.Moves);
    }

    [Fact]
    public void MovingOneShotTargetDoesNotRestartSearchDuringAnAlreadyLaunchedJump()
    {
        var h = new Harness("AdaptiveWalking"); h.Set("_targetId", 44UL);
        var points = new List<Vector3>();
        var runner = new GroundRouteRunner([new(Vector3.Zero, [new(2.5f, 0.8f, 0)], true)],
            p => { points = p; return true; }, () => { }, () => points, () => false,
            (_, _) => true, (_, _) => true, _ => true, _ => false);
        runner.Update(Vector3.Zero, false, DateTime.UtcNow);
        h.Set("_groundRunner", runner); h.Set("_groundJumping", new Func<bool>(() => true));
        h.PlayerPosition = new(1, 1.4f, 0); h.TargetPosition = new(30, 0, 0);
        h.Tick("AdaptiveWalking");
        Assert.Same(runner, h.Get("_groundRunner")); Assert.Null(h.Get("_heightPath")); Assert.Null(h.Get("_adaptiveGround"));
        Assert.True(h.Walk.IsActive); Assert.Equal(0, h.Get("_targetRepaths"));
    }

    [Fact]
    public void CloseFollowTargetCancelsAnUnlaunchedJumpInsteadOfJumpingAway()
    {
        var h = new Harness { TargetPosition = new(2, 0, 0) }; h.Follow();
        h.Set("_groundSegmentClear", new Func<Vector3, Vector3, bool>((_, _) => true));
        var jumps = 0;
        var runner = new GroundRouteRunner([new(Vector3.Zero, [new(2.5f, 0.8f, 0)], true)],
            _ => true, () => { }, () => [], () => false, (_, _) => true, (_, _) => true,
            _ => { ++jumps; return true; }, _ => false);
        h.Set("_followRunner", runner); h.Tick("Follow");
        Assert.Equal(0, jumps); Assert.Null(h.Get("_followRunner")); Assert.True(h.Walk.IsFollowing);
    }
}
