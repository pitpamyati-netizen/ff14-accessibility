using System.Text.Json;

namespace FF14Accessibility.Services;

public static partial class AccessibilityStrings
{
    private sealed record ClassDescriptionTranslation(string Source, string Russian);
    private static readonly Lazy<Dictionary<uint, ClassDescriptionTranslation>> ClassDescriptions = new(() =>
    {
        using var stream = typeof(AccessibilityStrings).Assembly.GetManifestResourceStream(
            "FF14Accessibility.Resources.RussianCharaMakeClasses.json");
        if (stream == null) return new();
        try { return JsonSerializer.Deserialize<Dictionary<uint, ClassDescriptionTranslation>>(stream) ?? new(); }
        catch (JsonException) { return new(); }
    });

    // Identity and the complete current English text guard the translation.
    // A changed game row keeps its own text rather than borrowing another class.
    internal static string CharaMakeClassDescription(uint classId, string englishSource, string gameText)
        => Loc.IsRussian && ClassDescriptions.Value.TryGetValue(classId, out var entry)
           && entry.Source == englishSource && !string.IsNullOrWhiteSpace(entry.Russian)
            ? entry.Russian : gameText;
}
