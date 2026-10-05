using System.Reflection;
using Dalamud.Plugin.Services;
using FF14Accessibility;
using FF14Accessibility.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Regression.Tests;

[Collection("Language")]
public sealed class ArmouryListSessionTests : IDisposable
{
    private readonly LanguageMode previous = Loc.Mode;
    public ArmouryListSessionTests() => Loc.Mode = LanguageMode.Russian;
    public void Dispose() => Loc.Mode = previous;

    [Fact]
    public void OpeningAndBrowsingReadsOneListAndConsumesBothNumlockVariants()
    {
        var f = new Fixture(); f.Tick();
        Assert.Contains("все предметы: 2", f.Speech.Last());
        Assert.DoesNotContain("фильтр", f.Speech.Last());
        f.Press(0x66); Assert.Contains("Body", f.Speech.Last()); Assert.False(f.Keys.Down[0x66]);
        f.Release(); f.Press(0x25); Assert.Contains("Axe", f.Speech.Last()); Assert.False(f.Keys.Down[0x25]);
    }

    [Theory]
    [InlineData(0x62, 5)] [InlineData(0x28, 5)]
    [InlineData(0x68, 10)] [InlineData(0x26, 10)]
    [InlineData(0x66, 1)] [InlineData(0x27, 1)]
    [InlineData(0x64, 11)] [InlineData(0x25, 11)]
    public void AllFourInventoryDirectionsSelectItemsInsteadOfSlotControls(int key, int index)
    {
        var f = new Fixture();
        f.Access.Items = ArmouryListModel.Containers.Select((type, i) =>
            new ArmouryListItem(type, i, (uint)(100 + i), 1, false, "instance " + i, "Item " + i)).ToList();
        f.Tick(); f.Press(key);
        Assert.Contains("Item " + index + ",", f.Speech.Last());
        Assert.DoesNotContain("фильтр", f.Speech.Last());
        Assert.DoesNotContain("категор", f.Speech.Last());
        Assert.False(f.Keys.Down[key]);
        Assert.True(f.Service.IsOpen);
        Assert.Equal(0, f.Access.CloseCount);
        f.Release(); f.Press(0x60);
        Assert.Equal(ArmouryListModel.Containers[index], f.Access.Requested!.Container);
        Assert.Equal(index, f.Access.Requested.Slot);
    }

    [Fact]
    public void OpeningImmediatelySelectsAnItemWithoutReadingTheControlInstructions()
    {
        var f = new Fixture(); f.Tick();
        Assert.Contains("Axe", f.Speech.Single());
        Assert.DoesNotContain(AccessibilityStrings.ArmouryListHelp, f.Speech.Single());
    }

    [Theory]
    [InlineData(false, false, false, "ArmouryBoard")]
    [InlineData(true, true, false, "ArmouryBoard")]
    [InlineData(true, false, true, "ArmouryBoard")]
    [InlineData(true, false, false, "SelectYesno")]
    [InlineData(true, false, false, "Inventory")]
    public void OtherWindowsTypingAndFocusLossKeepTheirKeyboard(bool active, bool typing, bool menu, string focus)
    {
        var f = new Fixture(); f.Tick(); f.Keys.Down[0x62] = true;
        f.Tick(active, typing, menu, focus);
        Assert.False(f.Service.HandleKeys(false)); Assert.True(f.Keys.Down[0x62]);
    }

    [Fact]
    public void ModifiersAndEscapeStayWithTheGame()
    {
        var f = new Fixture(); f.Tick(); f.Keys.Down[0x62] = true; f.Keys.Down[0x1B] = true; f.Tick();
        Assert.False(f.Service.HandleKeys(true)); Assert.True(f.Keys.Down[0x62]); Assert.True(f.Keys.Down[0x1B]);
    }

    [Fact]
    public void IncompleteContainersDoNotProduceAPartialListOrAnAction()
    {
        var f = new Fixture(); f.Access.Ready = false; f.Tick();
        Assert.Contains("загружается", f.Speech.Last()); f.Press(0x60);
        Assert.Null(f.Access.Requested);
        f.Release(); f.Access.Ready = true; f.Now += TimeSpan.FromSeconds(1); f.Tick();
        Assert.Contains("все предметы: 2", f.Speech.Last());
    }

    [Fact]
    public void EmptyArmouryIsReadableButCannotOpenAnItemMenu()
    {
        var f = new Fixture(); f.Access.Items.Clear(); f.Tick(); f.Press(0x60);
        Assert.Null(f.Access.Requested); Assert.Contains(AccessibilityStrings.MenuEmpty, f.Speech.Last());
    }

    [Fact]
    public void NativeMenuOpensForTheExactSelectedContainerAndSlotAndGetsNewPresses()
    {
        var f = new Fixture(); f.Tick(); f.Press(0x66); f.Release(); f.Press(0x60);
        Assert.Equal(InventoryType.ArmoryBody, f.Access.Requested!.Container);
        Assert.Equal(17, f.Access.Requested.Slot);
        f.Access.ContextVisible = true; f.Keys.Down[0x60] = true; f.Tick(focus: "ContextMenu");
        Assert.False(f.Keys.Down[0x60]); Assert.False(f.Service.OwnsInput);
        f.Keys.Down[0x60] = false; f.Tick(focus: "ContextMenu");
        f.Keys.Down[0x60] = true; f.Tick(focus: "ContextMenu");
        Assert.True(f.Keys.Down[0x60]); Assert.False(f.Service.HandleKeys(false));
    }

    [Fact]
    public void WaitingForContextCannotActivateTheNativeSlotUnderneath()
    {
        var f = new Fixture(); f.Tick(); f.Press(0x60); f.Release();
        Assert.True(f.Service.OwnsInput); Assert.False(f.Service.IsBrowsing);
        f.Press(0x62); Assert.False(f.Keys.Down[0x62]); Assert.Equal(1, f.Access.OpenCount);
    }

    [Fact]
    public void AfterNativeActionTheListRefreshesAndKeepsTheNearestRemainingRow()
    {
        var f = new Fixture(); f.Tick(); f.Press(0x60);
        f.Access.ContextVisible = true; f.Tick(focus: "ContextMenu");
        f.Access.Items.RemoveAt(0); f.Access.ContextVisible = false; f.Release();
        Assert.True(f.Service.IsBrowsing); Assert.Contains("Body", f.Speech.Last());
        f.Press(0x60); Assert.Equal(17, f.Access.Requested!.Slot);
    }

    [Theory]
    [InlineData("replacement")] [InlineData("logout")] [InlineData("zone")]
    [InlineData("closed")] [InlineData("newwindow")]
    public void ChangedStateCancelsOurPendingNativeMenu(string change)
    {
        var f = new Fixture(); f.Tick(); f.Press(0x60); f.Access.ContextVisible = true; f.Tick(focus: "ContextMenu");
        switch (change)
        {
            case "replacement": f.Access.Items[0] = f.Access.Items[0] with { Instance = "new" }; break;
            case "logout": f.LoggedIn = false; break;
            case "zone": f.Territory++; break;
            case "closed": f.Access.Window = default; break;
            case "newwindow": f.Access.Window = new(200, 138); break;
        }
        f.Tick(); Assert.Equal(1, f.Access.CancelCount);
    }

    [Fact]
    public void ContextTimeoutRecoversAndReportsFailure()
    {
        var f = new Fixture(); f.Tick(); f.Press(0x60); f.Release();
        f.Now += TimeSpan.FromSeconds(3); f.Tick();
        Assert.True(f.Service.IsBrowsing); Assert.Equal(AccessibilityStrings.ArmouryListMenuFailed, f.Speech.Last());
    }

    [Fact]
    public void DescriptionRechecksTheLiveItemInsteadOfReadingTheRemovedRow()
    {
        var f = new Fixture(); f.Tick(); f.Access.Items.RemoveAt(0); f.Service.ReadSelected(true);
        Assert.Contains("Body", f.Speech.Last()); Assert.Contains("description 101", f.Speech.Last());
        Assert.DoesNotContain("description 100", f.Speech.Last());
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void AChangedSelectionCannotUseTheSameConfirmPressForADifferentItem(bool removed)
    {
        var f = new Fixture(); f.Tick();
        if (removed) f.Access.Items.RemoveAt(0);
        else f.Access.Items[0] = f.Access.Items[0] with { Instance = "replaced" };
        f.Press(0x60);
        Assert.Equal(0, f.Access.OpenCount);
        Assert.Equal(AccessibilityStrings.ArmouryListItemChanged, f.Speech.Last());
        f.Release(); f.Press(0x60); Assert.Equal(1, f.Access.OpenCount);
    }

    [Fact]
    public void DescriptionIncludesConditionAndGearsetWarningForThisExactInstance()
    {
        var f = new Fixture();
        f.Access.Items[0] = f.Access.Items[0] with { Detail = "В комплекте. Прочность 37%." };
        f.Tick(); f.Service.ReadSelected(true);
        Assert.Contains("Прочность 37%", f.Speech.Last()); Assert.Contains("В комплекте", f.Speech.Last());
        f.Press(0x66); f.Release(); f.Service.ReadSelected(true);
        Assert.DoesNotContain("Прочность 37%", f.Speech.Last());
    }

    [Theory]
    [InlineData(0x6E)] [InlineData(0x2E)]
    public void DeleteClosesTheArmouryAndReopeningStartsAFreshList(int key)
    {
        var f = new Fixture(); f.Tick(); f.Press(key);
        Assert.False(f.Service.IsOpen); Assert.Equal(1, f.Access.CloseCount);
        f.Access.Window = new(100, 137); f.Release(); Assert.True(f.Service.IsBrowsing);
        Assert.Contains("все предметы: 2", f.Speech.Last());
    }

    private sealed class Fixture
    {
        internal readonly Access Access = new();
        internal readonly ShopQuantityTests.KeyStateProxy Keys;
        internal readonly List<string> Speech = [];
        internal readonly ArmouryListService Service;
        internal DateTime Now = new(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);
        internal bool LoggedIn = true;
        internal uint Territory = 133;
        internal Fixture()
        {
            var keys = DispatchProxy.Create<IKeyState, ShopQuantityTests.KeyStateProxy>();
            Keys = (ShopQuantityTests.KeyStateProxy)(object)keys;
            var log = NavigationStopTests.Proxy.Of<IPluginLog>(_ => null);
            Service = new(Access, keys, log, () => LoggedIn, () => Territory,
                id => "description " + id, Speech.Add, () => Now);
        }
        internal void Tick(bool active = true, bool typing = false, bool menu = false, string? focus = "ArmouryBoard")
            => Service.Update(active, typing, menu, focus);
        internal void Press(int key) { Keys.Down[key] = true; Tick(); Service.HandleKeys(false); }
        internal void Release() { Array.Clear(Keys.Down); Tick(); }
    }

    private sealed class Access : IArmouryListAccess
    {
        public ArmouryListWindow Window { get; set; } = new(100, 137);
        public bool ContextVisible { get; set; }
        internal bool Ready = true;
        internal int OpenCount, CancelCount, CloseCount;
        internal ArmouryListItem? Requested;
        internal List<ArmouryListItem> Items = [
            new(InventoryType.ArmoryMainHand, 8, 100, 1, false, "a", "Axe"),
            new(InventoryType.ArmoryBody, 17, 101, 1, true, "b", "Body")];
        public bool TryCollect(out List<ArmouryListItem> items) { items = [.. Items]; return Ready; }
        public bool OpenContext(ArmouryListWindow window, ArmouryListItem expected)
        { Requested = expected; OpenCount++; return Ready && Window == window && Matches(expected); }
        public bool ContextMatches(ArmouryListWindow window, ArmouryListItem expected)
            => Window == window && Requested == expected && Matches(expected);
        private bool Matches(ArmouryListItem expected)
            => Items.Any(i => ArmouryListModel.CanAct(expected, i));
        public void CloseContext(ArmouryListWindow window, ArmouryListItem expected)
        { if (Requested != expected) return; CancelCount++; ContextVisible = false; Requested = null; }
        public void Close(ArmouryListWindow window) { if (window == Window) { CloseCount++; Window = default; } }
    }
}
