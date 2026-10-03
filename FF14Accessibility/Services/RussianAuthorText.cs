using System.Text;

namespace FF14Accessibility.Services;

/// <summary>Speech-only translation of measured author additions; raw search keys stay intact.</summary>
internal static class RussianAuthorText
{
    private static readonly Lazy<RussianDescriptionCatalog?> Catalog = new(() =>
    {
        try { return RussianDescriptionCatalog.Load("RussianAuthorText"); }
        catch { return null; }
    });

    internal static string Translate(string sheet, uint id, ReadOnlySpan<byte> english, string fallback)
    {
        var translated = Catalog.Value?.Find(sheet, id, english, Loc.IsRussian);
        return translated == null ? fallback : Encoding.UTF8.GetString(translated);
    }

    internal static string PlaceName(uint id, string fallback)
        => Loc.IsRussian && RussianPlaceNames.PlaceName(id) is { } russian ? russian : fallback;
}
