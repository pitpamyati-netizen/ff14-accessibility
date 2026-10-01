namespace FF14Accessibility.Services;

public static partial class AccessibilityStrings
{
    public static string AdaptiveGroundSearching => L(
        "Ich suche einen anderen Weg.", "Searching for another route.", "Ищу другой маршрут.");
    public static string AdaptiveGroundWalking(string name) => L(
        $"Ich folge dem neuen Weg zu {name}.", $"Following the new route to {name}.", $"Иду по новому маршруту к {name}.");
    public static string AdaptiveGroundLanded => L(
        "Sprung geschafft.", "Jump completed.", "Прыжок выполнен.");
}
