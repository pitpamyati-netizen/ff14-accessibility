using System.Numerics;

namespace FF14Accessibility.Services;

internal sealed record GroundLeg(Vector3 From, List<Vector3> Points, bool Jump = false)
{
    internal Vector3 End => Points[^1];
    internal float Cost => GroundTraversal.Length(From, Points) + (Jump ? 3 : 0);
}

internal sealed record GroundFailure(Vector3 From, Vector3 To, bool Jump)
{
    internal bool Blocks(Vector3 from, Vector3 to, bool jump)
    {
        if (Jump != jump || MathF.Abs(from.Y - From.Y) > 1) return false;
        if (jump) return Vector3.Distance(from, From) < 0.8f && Vector3.Distance(to, To) < 0.8f;
        // The failed edge is ahead of the player, not a forbidden circle around
        // the player that would also prevent stepping sideways out of trouble.
        var delta = To - From;
        delta.Y = 0;
        if (delta.LengthSquared() < 0.01f) return false;
        var center = From + Vector3.Normalize(delta) * MathF.Min(delta.Length() * 0.75f, 1);
        return GroundDetour.SegmentNear(from, to, center);
    }
}

internal static class GroundTraversal
{
    internal const float MaxJumpDistance = 2.8f;
    internal const float MaxJumpRise = 1.0f;
    internal const float MaxJumpDrop = 1.0f;

    internal static float Length(Vector3 from, IReadOnlyList<Vector3> points)
    {
        var length = 0f;
        foreach (var p in points) { length += Vector3.Distance(from, p); from = p; }
        return length;
    }

    internal static List<Vector3>? Walk(Vector3 from, Vector3 to,
        Func<Vector3, float, float, Vector3?> nearest, Func<Vector3, Vector3, bool> clear)
    {
        if (!HeightPath.Finite(from) || !HeightPath.Finite(to)) return null;
        var steps = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(from, to) / 0.35f));
        if (steps > 32) return null;
        var previous = from;
        var points = new List<Vector3>();
        for (var i = 1; i <= steps; ++i)
        {
            var sample = Vector3.Lerp(from, to, (float)i / steps);
            var floor = nearest(sample, 0.45f, 0.65f);
            if (floor is not { } p || !HeightPath.Finite(p) || Vector3.Distance(p, sample) > 0.5f
                || !WalkStep(previous, p) || !clear(previous, p)) return null;
            points.Add(p); previous = p;
        }
        return Vector3.Distance(previous, to) <= 0.35f ? points : null;
    }

    internal static bool WalkStep(Vector3 from, Vector3 to)
    {
        var rise = MathF.Abs(to.Y - from.Y);
        return HeightPath.Finite(from) && HeightPath.Finite(to)
            && rise <= 0.35f && (rise <= 0.3f || rise <= GroundDetour.FlatDistance(from, to) * 1.45f);
    }

    internal static bool SupportedWalk(Vector3 from, Vector3 to, Func<Vector3, Vector3?> support)
    {
        if (!HeightPath.Finite(from) || !HeightPath.Finite(to) || support(from) is not { } previous
            || !HeightPath.Finite(previous)) return false;
        var count = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(from, to) / 0.35f));
        if (count > 12) return false;
        for (var i = 1; i <= count; ++i)
        {
            var sample = Vector3.Lerp(from, to, i / (float)count);
            if (support(sample) is not { } floor || !HeightPath.Finite(floor)
                || Vector3.Distance(floor, sample) > 0.55f || !WalkStep(previous, floor)) return false;
            previous = floor;
        }
        return true;
    }

    internal static bool Jump(Vector3 from, Vector3 to,
        Func<Vector3, float, float, Vector3?> nearest, Func<Vector3, Vector3, bool> clear)
    {
        var flat = GroundDetour.FlatDistance(from, to);
        var rise = to.Y - from.Y;
        if (!HeightPath.Finite(from) || !HeightPath.Finite(to) || flat < 0.6f
            || flat > MaxJumpDistance || rise > MaxJumpRise || rise < -MaxJumpDrop) return false;
        // Require a landing patch, not a single point on an eroded ledge. The
        // conservative envelope is a capability limit, not a physics prediction.
        foreach (var offset in new[] { Vector3.Zero, new Vector3(0.3f, 0, 0), new(-0.3f, 0, 0),
                     new Vector3(0, 0, 0.3f), new(0, 0, -0.3f) })
        {
            var sample = to + offset;
            if (nearest(sample, 0.2f, 0.3f) is not { } floor
                || !HeightPath.Finite(floor) || Vector3.Distance(floor, sample) > 0.2f) return false;
        }
        var previous = from;
        for (var i = 1; i <= 12; ++i)
        {
            var t = i / 12f;
            var sample = Vector3.Lerp(from, to, t) + new Vector3(0, 4 * 1.1f * t * (1 - t), 0);
            if (!clear(previous, sample)) return false;
            previous = sample;
        }
        return true;
    }
}
