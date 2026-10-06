using System.Reflection;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FF14Accessibility;
using FF14Accessibility.Services;
using Lumina;
using Lumina.Excel.Sheets;

if (args.Length != 2) throw new ArgumentException("CrossZoneCheck <game/sqpack> <report.json>");
using var game = new GameData(args[0]);
var data = new GameDataReader(game);
var log = DispatchProxy.Create<IPluginLog, QuietLog>();
var client = DispatchProxy.Create<IClientState, ZoneState>();
var state = (ZoneState)client;
var places = new PlacesService(data, client, log);
var entrances = new InteriorEntranceService(data, log).All;
Loc.Mode = LanguageMode.Russian;
var checks = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; }
Check(entrances.Count > 0, "No entrance links were loaded.");
var guard = entrances.Single(e => e.SourceMapId == 2 && e.TargetMapId == 68 && e.BaseId == 1000423);
var level = data.GetExcelSheet<Level>().GetRow(3861082);
Check(guard.Position == new System.Numerics.Vector3(level.X, level.Y, level.Z), "Guard source position lost.");
Check(data.GetExcelSheet<Quest>().GetRow(65983).QuestListenerParams.Any(p => p.Listener == guard.BaseId),
    "Recorded quest no longer links to this guard.");
state.Map = 2; state.Territory = 132;
var first = places.FindFirstHopToMap(68, out var hops);
Check(first != null && first.Position == guard.Position && hops == 1, "Recorded New Gridania -> First Bow route failed.");
Check(first is { IsZoneTransition: false }, "Interaction entrance must not trigger a border push.");
Check(places.GetHopDistances().GetValueOrDefault(68u) == 1, "Route ranking disagrees with the entrance route.");
var outside = places.FindFirstHopToMap(4, out var outsideHops);
Check(outside is { IsZoneTransition: true, TargetMapId: 4 } && outsideHops == 1, "Ordinary Central Shroud exit changed.");
state.Map = 3; state.Territory = 133;
var fromOldGridania = places.FindFirstHopToMap(68, out var oldHops);
Check(fromOldGridania is { IsZoneTransition: true, TargetMapId: 2 } && oldHops == 2, "Multi-zone entrance route failed.");
Check(places.FindLocalEntranceToMap(68) == null, "Foreign source entrance leaked across zones.");
state.Map = 68; state.Territory = 204;
var exit = places.FindFirstHopToMap(2, out var exitHops);
var exitObject = entrances.Single(e => e.SourceMapId == 68 && e.TargetMapId == 2 && e.BaseId == 2001215);
Check(exit != null && exit.Position == exitObject.Position && exitHops == 1, "Native EObj -> Warp exit failed.");
Check(places.FindFirstHopToMap(68, out _) == null, "Same-map target created an unnecessary entrance.");
Check(places.FindFirstHopToMap(0, out _) == null, "Unknown map created a route.");
Check(places.FindFirstHopToMap(uint.MaxValue, out _) == null, "Missing map created a route.");
var checkedSources = 0;
state.Map = 3; state.Territory = 133;
var lotusGuard = entrances.Single(e => e.SourceMapId == 3 && e.TargetMapId == 69 && e.BaseId == 1000460);
var lotus = places.FindFirstHopToMap(69, out var lotusHops);
Check(lotus is { TypeLabel: "Entrance", IsZoneTransition: false } && lotus.Position == lotusGuard.Position && lotusHops == 1,
    "Recorded Old Gridania -> Lotus guard route failed.");
state.Map = 28; state.Territory = 179;
var fromInn = places.FindFirstHopToMap(69, out var innHops);
var innDoor = places.FindLocalEntranceOnRoute(69);
Check(fromInn is { TypeLabel: "Entrance", TargetMapId: 2 } && innHops == 3,
    "Recorded inn -> New Gridania -> Old Gridania -> Lotus route failed.");
Check(innDoor is { BaseId: 2000087, LevelType: 45 } && fromInn?.Position == innDoor.Position,
    "Inn route did not select the exact local exit door.");
Check(places.GetHopDistances().GetValueOrDefault(69u) == innHops, "Inn route/ranking disagreement.");
var stopwatch = System.Diagnostics.Stopwatch.StartNew();
var checkedMaps = 0;
var service = new InteriorEntranceService(data, log);
foreach (var map in data.GetExcelSheet<Map>())
{
    if (map.RowId == 0 || map.TerritoryType.RowId == 0) continue;
    service.ForMap(map.RowId);
    checkedMaps++;
}
var allEntrances = service.All.ToArray();
stopwatch.Stop();
Check(allEntrances.Any(e => e.SourceMapId == 28 && e.BaseId == 2000087), "Full layout scan lost the inn door.");
foreach (var group in allEntrances.GroupBy(e => e.SourceMapId))
{
    state.Map = group.Key;
    state.Territory = (ushort)places.GetTerritoryOfMap(group.Key);
    foreach (var entrance in group)
    {
        var route = places.FindFirstHopToMap(entrance.TargetMapId, out var count);
        Check(route != null && count == 1, $"Direct entrance missing: {group.Key} -> {entrance.TargetMapId}");
        if (route is { IsZoneTransition: false })
            Check(group.Any(e => e.TargetMapId == entrance.TargetMapId && e.Position == route.Position), "Entrance source mismatch.");
        Check(places.GetHopDistances().GetValueOrDefault(entrance.TargetMapId) == count, "Full route/ranking disagreement.");
    }
    checkedSources++;
}
File.WriteAllText(args[1], JsonSerializer.Serialize(new { Passed = true, Checks = checks,
    Entrances = allEntrances.Length, SourceMaps = checkedSources, MapsScanned = checkedMaps,
    FullScanMs = stopwatch.ElapsedMilliseconds, RecordedQuests = new[] { 65983, 65985 },
    Guard = guard, LotusGuard = lotusGuard, InnDoor = innDoor, AllEntrances = allEntrances,
    NativeFfxivChecked = false }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
Console.WriteLine($"PASS: {checks} assertions, {allEntrances.Length} verified entrances in {checkedSources} source maps, {checkedMaps} maps scanned ({stopwatch.ElapsedMilliseconds} ms); both recorded quests and ordinary exits. Live game not tested.");

public class QuietLog : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.ReturnType == typeof(void)
        ? null : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
}
public class ZoneState : DispatchProxy
{
    public uint Map;
    public ushort Territory;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    { "get_MapId" => Map, "get_TerritoryType" => Territory, _ => throw new NotSupportedException(method.Name) };
}
