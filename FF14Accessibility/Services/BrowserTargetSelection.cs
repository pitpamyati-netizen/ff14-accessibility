using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;

namespace FF14Accessibility.Services;

internal static class BrowserTargetSelection
{
    internal static bool IsWorldObject(IGameObject? obj) => obj?.ObjectKind
        is ObjectKind.EventObj or ObjectKind.Treasure or ObjectKind.GatheringPoint or ObjectKind.Aetheryte;

    internal static IGameObject? FindExact(IEnumerable<IGameObject> objects, ulong id)
        => id == 0 ? null : objects.FirstOrDefault(o => o.GameObjectId == id);

    internal static bool ConfirmWorldObject(ITargetManager targets, IGameObject obj,
        Action<IGameObject> nativeConfirm)
    {
        if (!IsWorldObject(obj) || !obj.IsTargetable || !Select(targets, obj)) return false;
        // The native interaction is for this exact object. Generic Confirm may
        // choose the nearest enemy again after soft-target/UI churn.
        nativeConfirm(obj);
        return true;
    }

    internal static bool Select(ITargetManager targets, IGameObject obj)
    {
        // Keep the native game checks. An explicit browser choice replaces an
        // older temporary selection only after the hard target was accepted.
        targets.Target = obj;
        if (targets.Target?.GameObjectId != obj.GameObjectId) return false;
        if (targets.SoftTarget != null) targets.SoftTarget = null;
        return targets.Target?.GameObjectId == obj.GameObjectId;
    }

    internal static int NextIndex(int current, int direction, int count)
    {
        if (count <= 0) return -1;
        if (current < 0 || current >= count) return direction < 0 ? count - 1 : 0;
        return (int)(((long)current + direction % count + count) % count);
    }
}
