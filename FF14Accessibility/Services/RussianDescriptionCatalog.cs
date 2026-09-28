using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace FF14Accessibility.Services;

/// <summary>Offline, version-checked SeStrings generated from XIV Rus XLIFF files.</summary>
internal sealed class RussianDescriptionCatalog
{
    private readonly Dictionary<string, Dictionary<uint, byte[][]>> _sheets;

    internal RussianDescriptionCatalog(Stream compressed)
    {
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: true);
        _sheets = JsonSerializer.Deserialize<Dictionary<string, Dictionary<uint, byte[][]>>>(gzip)
                  ?? throw new InvalidDataException("Empty Russian description catalog.");
    }

    internal static RussianDescriptionCatalog Load(string resourceName = "RussianDescriptions")
    {
        using var resource = typeof(RussianDescriptionCatalog).Assembly.GetManifestResourceStream(
            $"FF14Accessibility.Resources.{resourceName}.json.gz")
            ?? throw new InvalidDataException("Russian description resource is missing.");
        return new RussianDescriptionCatalog(resource);
    }

    internal byte[]? Find(string sheet, uint id, ReadOnlySpan<byte> currentEnglish, bool russian)
    {
        if (!russian || !_sheets.TryGetValue(sheet, out var rows)
            || !rows.TryGetValue(id, out var pair) || pair.Length != 2)
            return null;
        // Row ids can survive a balance patch while potency/duration changes.
        // Never apply a translation to a different source revision of the tooltip.
        return currentEnglish.SequenceEqual(pair[0]) ? pair[1] : null;
    }
}
