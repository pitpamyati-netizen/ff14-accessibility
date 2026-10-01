namespace FF14Accessibility.Services;

public static partial class AccessibilityStrings
{
    public static string GroundPathSearching => L("Prüfe den begehbaren Weg.",
        "Checking the walkable route.", "Проверяю проходимость пути.");
    public static string GroundPathUnavailable => L("Kein durchgehender begehbarer Weg gefunden. Automatische Bewegung gestoppt.",
        "No continuous walking route found. Automatic movement stopped.",
        "Не удалось найти непрерывный пеший путь. Автоматическое движение остановлено.");
    public static string NavigationInvalidPosition => L("Ungültige Position. Automatische Bewegung gestoppt.",
        "Invalid position. Automatic movement stopped.", "Неверные координаты. Автоматическое движение остановлено.");
    public static string NavigationTargetGone(string name) => L($"{name} ist nicht mehr verfügbar. Bewegung gestoppt.",
        $"{name} is no longer available. Movement stopped.", $"Цель {name} больше недоступна. Движение остановлено.");
    public static string NavigationTargetMoving => L("Das Ziel bewegt sich weiter. Bewegung gestoppt; benutze den Folgemodus.",
        "The target keeps moving. Movement stopped; use follow mode.",
        "Цель продолжает перемещаться. Движение остановлено; используй режим следования.");
    public static string NavigationAirborneNoRoute => L("Du bist in der Luft. Kein geeigneter Flugweg verfügbar. Lande vor einem Bodenweg.",
        "You are airborne. No suitable flight route is available. Land before starting a ground route.",
        "Ты в воздухе. Подходящий воздушный путь недоступен. Для пешего пути сначала приземлись.");
    public static string NavigationFlightUnavailable => L("Kein sicherer vollständiger Flugweg verfügbar. Bewegung gestoppt.",
        "No complete flight route is available. Movement stopped.",
        "Полный воздушный путь недоступен. Движение остановлено.");
    public static string NavigationLandingFailed => L("Landung nicht abgeschlossen. Du bist noch in der Luft. Bewegung gestoppt.",
        "Landing did not complete. You are still airborne. Movement stopped.",
        "Приземлиться не удалось. Ты всё ещё в воздухе. Движение остановлено.");
    public static string NavigationFollowBlocked => L("Kein Fortschritt beim Folgen. Automatische Bewegung gestoppt.",
        "No progress while following. Automatic movement stopped.",
        "При следовании нет продвижения. Автоматическое движение остановлено.");
    public static string NavigationPathBlocked => L("Der Weg ist jetzt blockiert. Bewegung gestoppt.",
        "The next step is blocked. Movement stopped.", "Дальнейший путь перекрыт. Движение остановлено.");
}
