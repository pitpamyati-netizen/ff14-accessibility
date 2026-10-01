using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace FF14Accessibility.Services;

/// <summary>Read-only collision checks on the framework thread. A nearby floor
/// does not establish that a wall between two mesh fragments is passable.</summary>
internal static class GroundPathCollision
{
    internal static unsafe bool Clear(Vector3 from, Vector3 to)
    {
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
        for (var ray = 0; ray < 2; ++ray)
        {
            var height = 0.8f + ray * 0.4f;
            if (BGCollisionModule.RaycastMaterialFilter(from + new Vector3(0, height, 0),
                    direction, out _, length)
                || BGCollisionModule.RaycastMaterialFilter(to + new Vector3(0, height, 0),
                    -direction, out _, length)) return false;
        }
        return true;
    }
}
