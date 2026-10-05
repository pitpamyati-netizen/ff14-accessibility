namespace FF14Accessibility.Services;

public static partial class AccessibilityStrings
{
    public static string ArmouryListTitle(int count) => L(
        $"Arsenaltruhe, alle Gegenstände: {count}", $"Armoury Chest, all items: {count}",
        $"Арсенал, все предметы: {count}");
    public static string ArmouryListHelp => L(
        "8 und 2: Gegenstand wählen. 5: Beschreibung. 6: wiederholen. 0: Spielmenü des Gegenstands. 4 oder Entf: schließen. Pos1 und Ende: erster und letzter Gegenstand.",
        "8 and 2: select an item. 5: description. 6: repeat. 0: game item menu. 4 or Delete: close. Home and End: first and last item.",
        "8 и 2: выбрать предмет. 5: описание. 6: повторить. 0: игровое меню предмета. 4 или Delete: закрыть. Home и End: первый и последний предмет.");
    public static string ArmouryListUpdating => L(
        "Die Arsenaltruhe wird geladen. Bitte warten.", "The Armoury Chest is loading. Please wait.",
        "Арсенал загружается. Подожди.");
    public static string ArmouryListItemChanged => L(
        "Der Gegenstand hat sich geändert. Bitte erneut wählen.",
        "The item has changed. Please select it again.",
        "Предмет изменился. Выбери его заново.");
    public static string ArmouryListMenuFailed => L(
        "Das Spielmenü dieses Gegenstands konnte nicht geöffnet werden.",
        "Could not open this item's game menu.", "Не удалось открыть игровое меню этого предмета.");
}
