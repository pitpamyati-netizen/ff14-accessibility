using System.Reflection;
using System.Text.Json;
using Dalamud.Game;
using FF14Accessibility;
using FF14Accessibility.Services;
using Lumina;
using Lumina.Excel.Sheets;

if (args.Length != 2) throw new ArgumentException("CharaMakeClassCheck <game/sqpack> <report.json>");
using var game = new GameData(args[0], new LuminaOptions { DefaultExcelLanguage = Lumina.Data.Language.English });
var data = new GameDataReader(game);
var type = typeof(CharaMakeReader).Assembly.GetType("FF14Accessibility.Services.CharaMakeClassText")!;
var read = type.GetMethod("Read", BindingFlags.Static | BindingFlags.NonPublic)!;
var cases = new List<object>();
// Explicit expectations separate from the implementation's class-to-Lobby map.
uint[] classes = [1, 2, 3, 4, 5, 6, 7, 26];
uint[] lobbyIds = [178, 180, 182, 184, 186, 188, 190, 192];
var weapons = game.Excel.GetSheet<CharaMakeClassEquip>().ToArray();
if (weapons.Length != 8 || weapons.Select(x => x.Weapon).Distinct().Count() != 8)
    throw new Exception("Starting weapons changed; check the class mapping.");
foreach (var language in Enum.GetValues<ClientLanguage>())
foreach (var mode in new[] { LanguageMode.Russian, LanguageMode.English })
{
    data.Language = language;
    Loc.Mode = mode;
    for (var i = 0; i < classes.Length; i++)
    {
        var equipment = weapons.Single(x => x.Class.RowId == classes[i]);
        var result = read.Invoke(null, [data, equipment.Weapon]) ?? throw new Exception("Class was not resolved.");
        var resultType = result.GetType();
        var id = (uint)resultType.GetProperty("ClassId")!.GetValue(result)!;
        var name = (string)resultType.GetProperty("Name")!.GetValue(result)!;
        var description = (string)resultType.GetProperty("Description")!.GetValue(result)!;
        var source = data.GetExcelSheet<Lobby>().GetRow(lobbyIds[i]);
        if (id != classes[i] || description.Length < 40 || description != source.Unknown1.ExtractText().Trim())
            throw new Exception($"Wrong description for {language}/{mode}/{classes[i]}.");
        if (mode == LanguageMode.Russian)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "[А-Яа-яЁё]"))
                throw new Exception($"Russian class name missing: {name}");
        }
        else if (!string.Equals(name, equipment.Class.Value.Name.ExtractText(), StringComparison.OrdinalIgnoreCase)
                 && !string.Equals(name, data.GetExcelSheet<ClassJob>().GetRow(id).Name.ExtractText(), StringComparison.OrdinalIgnoreCase))
            throw new Exception($"Wrong game-language name: {name}");
        cases.Add(new { ClientLanguage = language.ToString(), PluginLanguage = mode.ToString(), ClassId = id,
            LobbyRow = lobbyIds[i], Weapon = $"{equipment.Weapon:X12}", Name = name, Description = description });
    }
}
File.WriteAllText(args[1], JsonSerializer.Serialize(new { CheckedAt = DateTimeOffset.Now, Cases = cases,
    InGameVerified = false, DescriptionsUseGameLanguage = true }, new JsonSerializerOptions { WriteIndented = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
Console.WriteLine($"Passed {cases.Count} class/description checks using installed game tables. Live selection and speech still require FFXIV.");
