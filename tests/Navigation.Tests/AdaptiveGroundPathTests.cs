using System.Numerics;
using FF14Accessibility.Services;

namespace Navigation.Tests;

public class AdaptiveGroundPathTests
{
    private static Task<List<Vector3>> Direct(Vector3 a, Vector3 b, CancellationToken _)
        => Task.FromResult(new List<Vector3> { a, b, b });
    private static Vector3? Plane(Vector3 p, float _, float y) => MathF.Abs(p.Y) <= y ? p with { Y = 0 } : null;
    private static void Complete(AdaptiveGroundPath path)
    {
        for (var tick = 0; tick < 50000 && !path.Done; ++tick) path.Update();
        Assert.True(path.Done);
    }

    [Fact]
    public void FiniteWallIsCircumventedWithoutAJump()
    {
        static bool Clear(Vector3 a, Vector3 b)
        {
            if ((a.X < 2.5f) == (b.X < 2.5f)) return true;
            var t = (2.5f - a.X) / (b.X - a.X);
            var z = a.Z + (b.Z - a.Z) * t;
            return MathF.Abs(z) > 1.5f;
        }
        using var path = new AdaptiveGroundPath(Vector3.Zero, new(6, 0, 0), 0.5f, Plane, Direct, Clear);
        Complete(path);
        Assert.NotNull(path.Result);
        Assert.False(path.ApproachOnly);
        Assert.All(path.Result, leg => Assert.False(leg.Jump));
        Assert.Contains(path.Result.SelectMany(l => l.Points), p => MathF.Abs(p.Z) > 1.5f);
        foreach (var leg in path.Result)
        {
            var previous = leg.From;
            foreach (var p in leg.Points) { Assert.True(Clear(previous, p)); previous = p; }
        }
    }

    [Fact]
    public void SmallVerticalLedgeRequiresExplicitJumpInsteadOfWalking()
    {
        static Vector3? Mesh(Vector3 p, float _, float extent)
        {
            var y = p.X < 1 ? 0 : 0.9f;
            return MathF.Abs(p.Y - y) <= extent ? p with { Y = y } : null;
        }
        static bool Clear(Vector3 a, Vector3 b) => (a.X < 1) == (b.X < 1);
        static Task<List<Vector3>> Query(Vector3 a, Vector3 b, CancellationToken _)
            => Task.FromResult(new List<Vector3> { a, Clear(a, b) ? b : a, b });
        using var path = new AdaptiveGroundPath(Vector3.Zero, new(4, 0.9f, 0), 0.5f, Mesh, Query, Clear,
            (a, b) => GroundTraversal.Jump(a, b, Mesh, (_, _) => true));
        Complete(path);
        Assert.NotNull(path.Result);
        Assert.False(path.ApproachOnly);
        Assert.Contains(path.Result, leg => leg.Jump);
        Assert.All(path.Result.Where(l => l.Jump), l => Assert.InRange(l.End.Y - l.From.Y, -1, 1));
    }

    [Fact]
    public void GroundTraceRejectsAnAbruptPointNineMeterStep()
    {
        static Vector3? Mesh(Vector3 p, float _, float extent)
        {
            var y = p.X < 0.6f ? 0 : 0.9f;
            return MathF.Abs(p.Y - y) <= extent ? p with { Y = y } : null;
        }
        Assert.Null(GroundTraversal.Walk(Vector3.Zero, new(1.25f, 0.9f, 0), Mesh, (_, _) => true));
        Assert.False(GroundTraversal.WalkStep(Vector3.Zero, new(0.25f, 0.9f, 0)));
    }

    [Fact]
    public void NativeSurfaceStepCannotHideVerticalLedgeBehindLooseFloorTolerance()
    {
        Assert.False(GroundTraversal.SupportedWalk(Vector3.Zero, new(0.625f, 0.9f, 0),
            p => p with { Y = p.X < 0.3f ? 0 : 0.9f }));
        Assert.True(GroundTraversal.SupportedWalk(Vector3.Zero, new(1.25f, 0.9f, 0),
            p => p)); // a genuinely continuous ordinary ramp
    }

    [Theory]
    [InlineData(0.25f, 0.25f, true)]
    [InlineData(0.4f, 0.34f, true)]
    [InlineData(0.1f, 0.34f, false)]
    [InlineData(0.2f, 0.7f, false)]
    public void GroundStepsRespectHeightAndSlope(float x, float y, bool expected)
        => Assert.Equal(expected, GroundTraversal.WalkStep(Vector3.Zero, new(x, y, 0)));

    [Theory]
    [InlineData(4, 0)] [InlineData(2, 4)] [InlineData(2, -4)] [InlineData(0, 1)]
    public void JumpEnvelopeRejectsLongOrVerticalFloorChanges(float x, float y)
        => Assert.False(GroundTraversal.Jump(Vector3.Zero, new(x, y, 0), (p, _, _) => p, (_, _) => true));

    [Fact]
    public void JumpNeedsWideEnoughLandingAndClearFeetAsWellAsHead()
    {
        Assert.False(GroundTraversal.Jump(Vector3.Zero, new(2, 0, 0),
            (p, _, _) => p.Z == 0 ? p : null, (_, _) => true));
        Assert.False(GroundTraversal.Jump(Vector3.Zero, new(2, 0, 0),
            (p, _, _) => p, (a, b) => a.X < 0.5f && b.X < 0.5f));
    }

    [Fact]
    public void CancellationDiscardsPendingNativeQuery()
    {
        var source = new TaskCompletionSource<List<Vector3>>();
        CancellationToken token = default;
        using var path = new AdaptiveGroundPath(Vector3.Zero, new(20, 0, 0), 1, Plane,
            (_, _, ct) => { token = ct; return source.Task; }, (_, _) => true);
        path.Update(); path.Update();
        Assert.True(token.CanBeCanceled);
        path.Dispose();
        Assert.True(token.IsCancellationRequested);
        source.SetResult([Vector3.Zero, new(20, 0, 0), new(20, 0, 0)]);
        path.Update(); Assert.Null(path.Result);
    }

    [Fact]
    public void NativeQueryUnavailableDoesNotTurnIntoBlindGraphWalking()
    {
        using var path = new AdaptiveGroundPath(Vector3.Zero, new(20, 0, 0), 1, Plane,
            (_, _, _) => null, (_, _) => true);
        Complete(path); Assert.Null(path.Result); Assert.Contains("IPC", path.LastFailure);
    }

    [Fact]
    public void BestCompleteRouteSurvivesAnEarlySearchDeadline()
    {
        using var path = new AdaptiveGroundPath(Vector3.Zero, new(20, 0, 0), 1, Plane, Direct, (_, _) => true);
        for (var i = 0; i < 10; ++i) path.Update();
        path.FinishWithBestAvailable();
        Assert.NotNull(path.Result); Assert.False(path.ApproachOnly);
        Assert.InRange(path.Result.Sum(l => l.Cost), 19, 21);
    }

    [Fact]
    public void PlannerImprovesAnInitiallyLongNativeRoute()
    {
        static Task<List<Vector3>> Query(Vector3 a, Vector3 b, CancellationToken _)
            => Task.FromResult(MathF.Abs(a.Z) < 0.5f
                ? new List<Vector3> { a, new(-5, 0, 0), new(-5, 0, 10), new(20, 0, 10), b, b }
                : new List<Vector3> { a, b, b });
        using var path = new AdaptiveGroundPath(Vector3.Zero, new(20, 0, 0), 0.5f, Plane, Query, (_, _) => true);
        Complete(path); Assert.NotNull(path.Result); Assert.False(path.ApproachOnly);
        Assert.True(path.Result.Sum(l => l.Cost) < 23);
    }

    [Fact]
    public void UsedPartialApproachCannotBeSelectedAgain()
    {
        var end = new Vector3(10, 0, 0);
        static Task<List<Vector3>> Query(Vector3 a, Vector3 b, CancellationToken _)
        {
            var end = new Vector3(10, 0, 0);
            return Task.FromResult(new List<Vector3> { a, b.X > 11 ? end : b, b });
        }
        static Vector3? Mesh(Vector3 p, float _, float y)
            => MathF.Abs(p.Y) <= y && MathF.Abs(p.Z) < 0.1f && p.X <= 10 ? p with { Y = 0 } : null;
        using var path = new AdaptiveGroundPath(Vector3.Zero, new(20, 0, 0), 1,
            (p, xz, y) => p.X == 20 ? p : Mesh(p, xz, y), Query, (_, _) => true,
            usedApproaches: [end]);
        Complete(path);
        Assert.Null(path.Result);
    }

    [Fact]
    public void FailedWalkBlocksForwardEdgeButAllowsSidewaysEscapeAndJump()
    {
        var failure = new GroundFailure(Vector3.Zero, new(1.5f, 0, 0), false);
        Assert.True(failure.Blocks(Vector3.Zero, new(2, 0, 0), false));
        Assert.False(failure.Blocks(Vector3.Zero, new(0, 0, 2), false));
        Assert.False(failure.Blocks(Vector3.Zero, new(2, 0, 0), true));
    }

    [Fact]
    public void FailedJumpIsExcludedAsAJumpOnly()
    {
        var failure = new GroundFailure(Vector3.Zero, new(2.5f, 0.9f, 0), true);
        Assert.True(failure.Blocks(Vector3.Zero, new(2.5f, 0.9f, 0), true));
        Assert.False(failure.Blocks(Vector3.Zero, new(2.5f, 0.9f, 0), false));
    }

    [Fact]
    public void UnreachableOpenSurfaceSearchHasFiniteBudget()
    {
        using var path = new AdaptiveGroundPath(Vector3.Zero, new(0, 14, 0), 1,
            (p, _, y) => p.Y > 10 ? p : Plane(p, 0, y),
            (a, b, _) => Task.FromResult(new List<Vector3> { a, a, b }), (_, _) => true);
        Complete(path);
        Assert.Null(path.Result); Assert.InRange(path.Expanded, 1, AdaptiveGroundPath.MaxNodes + 1);
    }

    [Fact]
    public void MeshProbesAreBoundedPerFrameDuringGraphExpansion()
    {
        var probes = 0;
        using var path = new AdaptiveGroundPath(Vector3.Zero, new(20, 0, 0), 1,
            (p, _, _) => { ++probes; return p; },
            (a, b, _) => Task.FromResult(new List<Vector3> { a, a, b }), (_, _) => true);
        for (var frame = 0; frame < 100; ++frame)
        {
            probes = 0; path.Update(); Assert.InRange(probes, 0, 24);
        }
    }
}
