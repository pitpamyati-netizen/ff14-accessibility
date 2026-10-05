using FF14Accessibility.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Regression.Tests;

public sealed class ArmouryListTests
{
    private static ArmouryListItem Item(InventoryType type = InventoryType.ArmoryHead,
        int slot = 0, uint id = 100, bool hq = false, string instance = "AA", string label = "Hat")
        => new(type, slot, id, 1, hq, instance, label);

    [Fact]
    public void AllTwelveNativeSectionsBecomeOneListWithoutGroupingOrIdDeduplication()
    {
        var model = new ArmouryListModel();
        model.Replace(ArmouryListModel.Containers.Select(type => Item(type)));
        Assert.Equal(12, model.Items.Count);
        Assert.Equal(12, model.Items.Select(i => i.Container).Distinct().Count());
        Assert.DoesNotContain(InventoryType.EquippedItems, ArmouryListModel.Containers);
        Assert.DoesNotContain(InventoryType.Inventory1, ArmouryListModel.Containers);
    }

    [Fact]
    public void EmptySlotsAndOtherInventoriesCannotEnterTheList()
    {
        var model = new ArmouryListModel();
        model.Replace([Item(), Item(slot: 1, id: 0), Item(slot: 2) with { Quantity = 0 },
            Item(InventoryType.EquippedItems), Item(InventoryType.Inventory1)]);
        Assert.Single(model.Items);
    }

    [Fact]
    public void IdenticalItemsAndBothQualitiesKeepTheirOwnPhysicalAddresses()
    {
        var model = new ArmouryListModel();
        model.Replace([Item(slot: 4), Item(slot: 8), Item(slot: 12, hq: true)]);
        Assert.Equal([4, 8, 12], model.Items.Select(i => i.Slot));
        model.Move(1); Assert.Equal(8, model.Selected!.Slot);
        model.Move(1); Assert.True(model.Selected!.HighQuality);
    }

    [Fact]
    public void RefreshPreservesTheSelectedInstanceWhenSortingOrTranslationChanges()
    {
        var model = new ArmouryListModel();
        model.Replace([Item(slot: 4, label: "A"), Item(slot: 8, label: "B")]);
        model.Move(1);
        model.Replace([Item(slot: 8, label: "А"), Item(slot: 4, label: "Б"), Item(slot: 0, label: "0")]);
        Assert.Equal(8, model.Selected!.Slot);
        Assert.Equal("А", model.Selected.Label);
    }

    [Fact]
    public void RemovedLastItemCannotLeaveTheCursorOutsideTheList()
    {
        var model = new ArmouryListModel();
        model.Replace([Item(slot: 1), Item(slot: 2), Item(slot: 3)]); model.End(true);
        Assert.True(model.Replace([Item(slot: 1)]));
        Assert.Equal(0, model.Cursor); Assert.Equal(1, model.Selected!.Slot);
        model.Replace([]); model.Move(-1); model.End(true);
        Assert.Null(model.Selected); Assert.Equal(0, model.Cursor);
    }

    [Theory]
    [InlineData("slot")] [InlineData("container")] [InlineData("id")]
    [InlineData("quality")] [InlineData("instance")] [InlineData("gone")]
    public void AnActionRejectsEveryStaleOrDifferentPhysicalItem(string change)
    {
        var original = Item();
        var current = change switch
        {
            "slot" => original with { Slot = 1 },
            "container" => original with { Container = InventoryType.ArmoryBody },
            "id" => original with { ItemId = 101 },
            "quality" => original with { HighQuality = true },
            "instance" => original with { Instance = "BB" },
            _ => null,
        };
        Assert.False(ArmouryListModel.CanAct(original, current));
        Assert.True(ArmouryListModel.CanAct(original, original));
    }

    [Fact]
    public void BrowsingWrapsAndFirstLastAndReopeningArePredictable()
    {
        var model = new ArmouryListModel();
        model.Replace([Item(slot: 1), Item(slot: 2)]);
        model.Move(-1); Assert.Equal(1, model.Cursor);
        model.Move(1); Assert.Equal(0, model.Cursor);
        model.End(true); Assert.Equal(1, model.Cursor);
        model.Clear(); model.Replace([Item(slot: 9)]);
        Assert.Equal(0, model.Cursor); Assert.Equal(9, model.Selected!.Slot);
    }
}
