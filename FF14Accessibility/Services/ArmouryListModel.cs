using FFXIVClientStructs.FFXIV.Client.Game;

namespace FF14Accessibility.Services;

internal sealed record ArmouryListItem(InventoryType Container, int Slot, uint ItemId,
    uint Quantity, bool HighQuality, string Instance, string Label, string Detail = "");

// A row identifies a physical item, never a displayed index or an icon.
internal sealed class ArmouryListModel
{
    internal const int Columns = 5;
    internal static readonly InventoryType[] Containers =
    [
        InventoryType.ArmoryMainHand, InventoryType.ArmoryHead, InventoryType.ArmoryBody,
        InventoryType.ArmoryHands, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets,
        InventoryType.ArmoryOffHand, InventoryType.ArmoryEar, InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist, InventoryType.ArmoryRings, InventoryType.ArmorySoulCrystal,
    ];

    internal IReadOnlyList<ArmouryListItem> Items { get; private set; } = [];
    internal int Cursor { get; private set; }
    internal ArmouryListItem? Selected => Items.Count == 0 ? null : Items[Cursor];

    internal bool Replace(IEnumerable<ArmouryListItem> items)
    {
        var previous = Selected;
        // The collector supplies the game's order. Translation must not move
        // items, and category controls never become rows in this grid.
        var fresh = items.Where(i => Containers.Contains(i.Container) && i.ItemId != 0 && i.Quantity > 0)
            .ToArray();
        Items = fresh;
        var same = previous == null ? -1 : Array.FindIndex(fresh, i => SameAddress(i, previous));
        Cursor = same >= 0 ? same : Math.Clamp(Cursor, 0, Math.Max(0, fresh.Length - 1));
        return previous != Selected;
    }

    internal void Move(int direction)
    {
        if (Items.Count > 0) Cursor = (Cursor + direction + Items.Count) % Items.Count;
    }

    internal void MoveRow(int direction)
    {
        if (Items.Count == 0) return;
        var rows = (Items.Count + Columns - 1) / Columns;
        var row = (Cursor / Columns + direction + rows) % rows;
        Cursor = Math.Min(row * Columns + Cursor % Columns, Items.Count - 1);
    }

    internal void End(bool last) => Cursor = last ? Math.Max(0, Items.Count - 1) : 0;
    internal void Clear() { Items = []; Cursor = 0; }
    internal static bool SameAddress(ArmouryListItem a, ArmouryListItem b)
        => a.Container == b.Container && a.Slot == b.Slot && a.ItemId == b.ItemId
            && a.HighQuality == b.HighQuality;
    internal static bool CanAct(ArmouryListItem expected, ArmouryListItem? current)
        => current != null && SameAddress(expected, current) && expected.Instance == current.Instance;
}
