using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FF14Accessibility.Services;

internal readonly record struct ArmouryListWindow(nint Address, ushort Id);

internal interface IArmouryListAccess
{
    ArmouryListWindow Window { get; }
    bool ContextVisible { get; }
    bool TryCollect(out List<ArmouryListItem> items);
    bool OpenContext(ArmouryListWindow window, ArmouryListItem expected);
    bool ContextMatches(ArmouryListWindow window, ArmouryListItem expected);
    void CloseContext(ArmouryListWindow window, ArmouryListItem expected);
    void Close(ArmouryListWindow window);
}

internal sealed unsafe class ArmouryListAccess(IGameGui gui, InventoryService inventory, GearInfoService gear) : IArmouryListAccess
{
    public ArmouryListWindow Window
    {
        get { var board = Visible("ArmouryBoard"); return board == null ? default : new((nint)board, board->Id); }
    }
    public bool ContextVisible => Visible("ContextMenu") != null;

    public bool TryCollect(out List<ArmouryListItem> items)
    {
        items = [];
        var manager = InventoryManager.Instance();
        if (manager == null) return false;
        foreach (var type in ArmouryListModel.Containers)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded || container->Items == null) { items.Clear(); return false; }
            for (var slot = 0; slot < container->Size; slot++)
            {
                var item = Read(container->Items + slot, type, slot);
                if (item != null) items.Add(item);
            }
        }
        return true;
    }

    private ArmouryListItem? Read(InventoryItem* item, InventoryType type, int slot)
    {
        var id = item->GetItemId();
        if (id == 0 || item->Quantity <= 0) return null;
        var hq = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
        var label = inventory.ResolveItemLabel(id) + (hq ? AccessibilityStrings.HighQuality : string.Empty);
        if (item->Quantity > 1) label = AccessibilityStrings.ItemQuantity(item->Quantity.ToString(), label);
        var brief = gear.DescribeGear(id, briefWhenWearable: true);
        if (brief.Length > 0) label += ", " + brief;
        var sets = RaptureGearsetModule.Instance();
        var registered = sets != null && sets->NumGearsets > 0 && sets->IsItemRegisteredToGearset(item);
        if (registered) label += AccessibilityStrings.InGearsetShort;
        var detail = registered ? AccessibilityStrings.InGearsetWarning : string.Empty;
        if (type != InventoryType.ArmorySoulCrystal)
        {
            var condition = item->GetConditionPercentage();
            if (condition < 100) label += ", " + AccessibilityStrings.ItemCondition(condition);
            detail += " " + AccessibilityStrings.ItemCondition(condition);
        }
        // Includes materia, condition, glamour and binding. Replacing the same
        // ID in the same slot cannot pass the native action's instance check.
        var instance = Convert.ToHexString(new ReadOnlySpan<byte>(item, sizeof(InventoryItem)));
        return new(type, slot, id, (uint)item->Quantity, hq, instance, label, detail.Trim());
    }

    private bool ItemMatches(ArmouryListItem expected)
    {
        var manager = InventoryManager.Instance();
        var container = manager == null ? null : manager->GetInventoryContainer(expected.Container);
        return container != null && container->IsLoaded && container->Items != null
            && expected.Slot >= 0 && expected.Slot < container->Size
            && ArmouryListModel.CanAct(expected, Read(container->Items + expected.Slot, expected.Container, expected.Slot));
    }

    public bool OpenContext(ArmouryListWindow window, ArmouryListItem expected)
    {
        if (Window != window || window.Address == 0 || ContextVisible || !ItemMatches(expected)) return false;
        var agent = AgentInventoryContext.Instance();
        if (agent == null) return false;
        agent->OpenForItemSlot(expected.Container, expected.Slot, 0, window.Id);
        return TargetMatches(window, expected) && ItemMatches(expected);
    }

    public bool ContextMatches(ArmouryListWindow window, ArmouryListItem expected)
        => Window == window && TargetMatches(window, expected) && ItemMatches(expected);

    private static bool TargetMatches(ArmouryListWindow window, ArmouryListItem expected)
    {
        var agent = AgentInventoryContext.Instance();
        return agent != null && agent->TargetInventoryId == expected.Container
            && agent->TargetInventorySlotId == expected.Slot && agent->OwnerAddonId == window.Id
            && agent->TargetInventorySlot != null && agent->TargetInventorySlot->GetItemId() == expected.ItemId;
    }

    public void CloseContext(ArmouryListWindow window, ArmouryListItem expected)
    {
        // Never close an unrelated item menu that took over while we waited.
        var agent = AgentInventoryContext.Instance();
        var context = Visible("ContextMenu");
        if (context != null && agent != null && agent->OwnerAddonId == window.Id
            && agent->TargetInventoryId == expected.Container && agent->TargetInventorySlotId == expected.Slot)
            context->Close(true);
    }

    public void Close(ArmouryListWindow window)
    {
        var board = Visible("ArmouryBoard");
        if (board != null && Window == window && window.Address != 0) board->Close(true);
    }

    private AtkUnitBase* Visible(string name)
    {
        var ptr = gui.GetAddonByName(name);
        var addon = ptr.IsNull ? null : (AtkUnitBase*)(nint)ptr;
        return addon != null && addon->IsVisible ? addon : null;
    }
}
