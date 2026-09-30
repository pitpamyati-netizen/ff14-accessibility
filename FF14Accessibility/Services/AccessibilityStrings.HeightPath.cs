using System;

namespace FF14Accessibility.Services;

public static partial class AccessibilityStrings
{
    public static string HeightPathSearching => L(
        "Prüfe den Weg zur Zielebene und suche einen begehbaren Zugang.",
        "Checking the route to the target's floor and looking for a walkable approach.",
        "Проверяю путь на высоту цели и ищу проходимый подход.");

    public static string HeightPathUnavailable(float rise) => L(
        $"Kein durchgehender Bodenweg zur Zielebene gefunden.{TargetHeight(rise)} Suche einen Aufgang, eine Treppe oder einen anderen Zugang.",
        $"No continuous walking route to the target's floor was found.{TargetHeight(rise)} Look for a ramp, stairs or another entrance.",
        $"Не удалось найти непрерывный пеший путь на высоту цели.{TargetHeight(rise)} Нужен подъём, лестница или другой вход.");

    public static string TargetHeight(float rise) => MathF.Abs(rise) < 1.5f ? string.Empty : L(
        $" Ziel {MathF.Abs(rise):F0} Meter {(rise > 0 ? "höher" : "tiefer")}.",
        $" Target {MathF.Abs(rise):F0} meters {(rise > 0 ? "above" : "below")}.",
        $" Цель на {MathF.Abs(rise):F0} метров {(rise > 0 ? "выше" : "ниже")}.");

    public static string WalkMeshEndsAtHeight(float horizontal, string direction, float rise) => L(
        "Hier endet der berechnete Weg." + TargetHeight(rise) +
        (horizontal < 1 ? " Waagerecht bist du schon am Ziel." : $" Waagerecht noch {horizontal:F0} Meter nach {direction}."),
        "The calculated path ends here." + TargetHeight(rise) +
        (horizontal < 1 ? " You are already at the target's horizontal position." : $" Horizontally, {horizontal:F0} meters to the {direction}."),
        "Здесь заканчивается рассчитанный путь." + TargetHeight(rise) +
        (horizontal < 1 ? " По горизонтали ты уже у цели." : $" По горизонтали осталось {horizontal:F0} метров на {direction}."));
}
