namespace FF14Accessibility.Services;

public static partial class AccessibilityStrings
{
    public static string FocusedItemUnavailable => L(
        "Gegenstand wird aktualisiert. Bitte noch einmal lesen.",
        "Item is updating. Please read again.",
        "Предмет обновляется. Повтори чтение.");
    public static string ArmouryMainHand => L("Haupthand", "Main hand", "Главная рука");
    public static string ArmourySlotFilter(string slot) => L(
        $"{slot}, Platzfilter", $"{slot}, slot filter", $"{slot}, фильтр слота");
    public static string SelectedObjectUnavailable(string name) => L(
        $"{name} kann noch nicht anvisiert werden. Geh näher heran.",
        $"{name} cannot be targeted yet. Move closer.",
        $"{name} пока нельзя выбрать целью. Подойди ближе.");
}
