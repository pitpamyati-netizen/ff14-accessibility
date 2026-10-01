using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace FF14Accessibility.Services;

/// <summary>Read-only collision checks on the framework thread. A nearby floor
/// does not establish that a wall between two mesh fragments is passable.</summary>
internal static class GroundPathCollision
{
    internal static string? LastFailure { get; private set; }

    /// <summary>Only live geometry beside the player can veto a plan. Remote
    /// doors/conditional collision and streamed scene data can change before
    /// arrival. Walking still checks every upcoming segment against Clear.</summary>
    internal static bool CheckNearby(Vector3 player, Vector3 from, Vector3 to,
        Func<Vector3, Vector3, bool> clear)
    {
        if (!HeightPath.Finite(player) || !HeightPath.Finite(from) || !HeightPath.Finite(to)) return false;
        const float radius = 1.5f;
        var delta = to - from;
        var lengthSquared = delta.LengthSquared();
        if (lengthSquared < 0.0025f) return true;
        var center = Vector3.Dot(player - from, delta) / lengthSquared;
        var closest = from + delta * Math.Clamp(center, 0, 1);
        if (Vector3.DistanceSquared(closest, player) > radius * radius) return true;
        var perpendicular = Vector3.DistanceSquared(from + delta * center, player);
        var half = MathF.Sqrt(MathF.Max(0, radius * radius - perpendicular) / lengthSquared);
        var begin = Math.Clamp(center - half, 0, 1);
        var end = Math.Clamp(center + half, 0, 1);
        return clear(from + delta * begin, from + delta * end);
    }

    internal static unsafe bool Clear(Vector3 from, Vector3 to)
        => ClearBody(from, to, [0.8f, 1.2f]);

    internal static unsafe bool WalkClear(Vector3 from, Vector3 to)
    {
        if (!Clear(from, to)) return false;
        // Nearby mesh seams can be genuinely walkable, but an actual hole in
        // the game floor must become an explicit jump rather than a walk.
        return GroundTraversal.SupportedWalk(from, to, ProbeSupport);
    }

    private static unsafe Vector3? ProbeSupport(Vector3 position)
    {
        var framework = Framework.Instance();
        if (framework == null || framework->BGCollisionModule == null || framework->BGCollisionModule->ShuttingDown) return null;
        return BGCollisionModule.RaycastMaterialFilter(position + new Vector3(0, 0.6f, 0), -Vector3.UnitY,
            out var hit, 1.2f) && HeightPath.Finite(hit.Point) && MathF.Abs(hit.Point.Y - position.Y) <= 0.55f
            && hit.Normal.Y >= 0.45f ? hit.Point : null;
    }

    internal static unsafe bool ClearJumpSegment(Vector3 from, Vector3 to)
        => ClearBody(from, to, [0.12f, 0.8f, 1.5f]);

    private static unsafe bool ClearBody(Vector3 from, Vector3 to, float[] heights)
    {
        LastFailure = null;
        if (!HeightPath.Finite(from) || !HeightPath.Finite(to)) return false;
        var framework = Framework.Instance();
        if (framework == null || framework->BGCollisionModule == null
            || framework->BGCollisionModule->ShuttingDown) return false;
        var delta = to - from;
        var length = delta.Length();
        if (length < 0.05f) return true;
        var direction = delta / length;
        // Foot-level rays would hit the very stairs being checked. Test the
        // body instead, retaining the segment's slope on uneven terrain.
        var side = Vector3.Cross(direction, Vector3.UnitY);
        if (side.LengthSquared() > 0.01f) side = Vector3.Normalize(side) * 0.25f;
        foreach (var height in heights)
        foreach (var offset in new[] { Vector3.Zero, side, -side })
        {
            if (BGCollisionModule.RaycastMaterialFilter(from + offset + new Vector3(0, height, 0),
                    direction, out var hit, length)
                || BGCollisionModule.RaycastMaterialFilter(to + offset + new Vector3(0, height, 0),
                    -direction, out hit, length))
            {
                LastFailure = $"from={from}, to={to}, rayHeight={height:F2}, hit={hit.Point}, normal={hit.Normal}, distance={hit.Distance:F3}, material={hit.Material:X}";
                return false;
            }
        }
        return true;
    }

    internal static unsafe bool HasLanding(Vector3 position)
    {
        if (!HeightPath.Finite(position)) return false;
        var framework = Framework.Instance();
        if (framework == null || framework->BGCollisionModule == null || framework->BGCollisionModule->ShuttingDown) return false;
        foreach (var offset in new[] { Vector3.Zero, new Vector3(0.25f, 0, 0), new(-0.25f, 0, 0),
                     new Vector3(0, 0, 0.25f), new(0, 0, -0.25f) })
            if (!BGCollisionModule.RaycastMaterialFilter(position + offset + new Vector3(0, 0.5f, 0),
                    -Vector3.UnitY, out var hit, 1)
                || !HeightPath.Finite(hit.Point) || MathF.Abs(hit.Point.Y - position.Y) > 0.35f || hit.Normal.Y < 0.65f)
                return false;
        return true;
    }

    internal static unsafe bool IsStanding(Vector3 position)
    {
        if (!HeightPath.Finite(position)) return false;
        var framework = Framework.Instance();
        if (framework == null || framework->BGCollisionModule == null || framework->BGCollisionModule->ShuttingDown) return false;
        return BGCollisionModule.RaycastMaterialFilter(position + new Vector3(0, 0.12f, 0), -Vector3.UnitY,
            out var hit, 0.24f) && HeightPath.Finite(hit.Point) && MathF.Abs(hit.Point.Y - position.Y) <= 0.08f
            && hit.Normal.Y >= 0.45f;
    }
}
