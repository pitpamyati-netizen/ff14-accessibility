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
state.Map = 2; state.Territory = 132;
var airship = places.FindFirstHopToMap(74, out var airshipHops);
var airshipNpc = places.FindLocalEntranceOnRoute(74);
Check(airship is { TypeLabel: "Entrance", TargetMapId: 74, IsZoneTransition: false }
    && airshipNpc is { BaseId: 1000106 } && airshipHops == 1,
    "Recorded Gridanian Envoy route must lead to Lionnellais for passage to Limsa's landing.");
var rogueTrip = places.FindFirstHopToMap(548, out var rogueHops);
Check(rogueTrip?.Position == airship?.Position && rogueHops > 1,
    "Recorded Stabbers in Yer Fambles must start with the airship, not refuse another area.");
state.Map = 74; state.Territory = 128;
var lift = places.FindFirstHopToMap(548, out var liftHops);
Check(lift != null && liftHops < rogueHops && places.FindLocalEntranceOnRoute(548) != null,
    "Arrival must continue through the local lift towards the Rogues' Guild.");
state.Map = 12; state.Territory = 129;
var guild = places.FindLocalEntranceOnRoute(548);
Check(guild is { BaseId: 1009944, TargetMapId: 548 }, "Same-territory guild route must select Lonwoerd.");
state.Map = 548; state.Territory = 129;
var guildExit = places.FindLocalEntranceOnRoute(2);
Check(guildExit is { BaseId: 2004936, TargetMapId: 12 }, "Return travel must leave through the local guild door.");
var landingRange = new TravelLayout(data, log).ForTerritory(128);
Check(landingRange.MapAt(new(-12.6917f, 91.4999f, -7.60297f)) == 74,
    "Airship PopRange's old map11 must be corrected by the actual landing range.");
Check(landingRange.Arrivals.ContainsKey(4158063), "Lift arrival missing from Level must be obtained from layout.");
Console.WriteLine($"Recorded travel: {airship?.Name} (NPC {airshipNpc?.BaseId}); {rogueHops} legs to guild; landing next: {lift?.Name}; guild NPC {guild?.BaseId}.");
var stopwatch = System.Diagnostics.Stopwatch.StartNew();
var checkedMaps = 0;
var service = new InteriorEntranceService(data, log);
foreach (var map in data.GetExcelSheet<Map>())
{
    if (map.RowId == 0 || map.TerritoryType.RowId == 0) continue;
    service.ForMap(map.RowId);
    var canonical = data.GetExcelSheet<Map>().GetRow(places.CanonicalMap(map.RowId));
    Check(map.TerritoryType.RowId == canonical.TerritoryType.RowId && map.Id.ExtractText() == canonical.Id.ExtractText()
        && map.SizeFactor == canonical.SizeFactor && map.OffsetX == canonical.OffsetX && map.OffsetY == canonical.OffsetY,
        "Physical map aliases must never merge different zones or floors.");
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
// Audit every Quest actor/object constant with a typed, located Level row.
// These include all phases; they are coverage evidence, not live quest runs.
var questActors = data.GetExcelSheet<Quest>().SelectMany(q => q.QuestParams)
    .Where(p => p.ScriptInstruction.ExtractText().StartsWith("ACTOR", StringComparison.Ordinal)
        || p.ScriptInstruction.ExtractText().StartsWith("EOBJECT", StringComparison.Ordinal))
    .Select(p => p.ScriptArg).Where(id => id != 0).ToHashSet();
var targetLevels = data.GetExcelSheet<Level>().Where(l => l.Type is 8 or 45 && questActors.Contains(l.Object.RowId)
    && l.Map.RowId != 0 && l.Territory.RowId != 0).ToArray();
state.Map = 2; state.Territory = 132;
var reachable = places.GetHopDistances();
var targetMaps = targetLevels.Select(l => l.Map.RowId).Distinct().ToArray();
var unreachable = targetMaps.Where(id => !reachable.ContainsKey(places.CanonicalMap(id))).ToArray();
var teleportMaps = data.GetExcelSheet<Aetheryte>().Where(a => a.IsAetheryte && !a.Invisible).Select(a => places.CanonicalMap(a.Map.RowId)).ToHashSet();
var withTeleport = unreachable.Where(id => places.GetHopDistancesTo(id).Keys.Any(teleportMaps.Contains)).ToArray();
var duties = new DutyEntranceService(data, client, places, log);
var withDuty = unreachable.Except(withTeleport).Where(id =>
{
    var target = targetLevels.First(l => l.Map.RowId == id);
    var goal = new QuestDestination("audit", "", new(target.X, target.Y, target.Z), 1,
        (ushort)target.Territory.RowId, id, false, QuestKind.Unknown, 0);
    var duty = QuestTravelHints.FindDuty(data, duties, places, goal, state.Map);
    if (duty == null) return false;
    var travel = QuestTravelHints.DutyGoal(goal, duty, state.Map);
    Check(travel.TargetBaseId != 0 && travel.TargetLevelType == 45
        && data.GetExcelSheet<EObj>().TryGetRow(travel.TargetBaseId, out _), "Duty route lost exact external door.");
    return reachable.ContainsKey(places.CanonicalMap(travel.MapId)) || places.GetHopDistancesTo(travel.MapId).Keys.Any(teleportMaps.Contains);
}).ToArray();
foreach(var id in targetMaps.Where(id => reachable.ContainsKey(places.CanonicalMap(id))))
{
    if (id == 2) continue;
    var firstHop = places.FindFirstHopToMap(id, out var count);
    Check(firstHop != null && count == reachable[places.CanonicalMap(id)], $"Quest actor map {id} route/ranking disagreement.");
}
var withDutyFinder = unreachable.Except(withTeleport).Except(withDuty).Where(id =>
    data.GetExcelSheet<InstanceContent>().Count(i => i.ContentFinderCondition.ValueNullable?.TerritoryType.RowId == places.GetTerritoryOfMap(id)) == 1).ToArray();
var stillUnmapped = unreachable.Except(withTeleport).Except(withDuty).Except(withDutyFinder).Select(id => new { Map = id,
    Territory = places.GetTerritoryOfMap(id), Name = places.GetMapName(id) }).ToArray();
File.WriteAllText(args[1], JsonSerializer.Serialize(new { Passed = true, Checks = checks,
    Entrances = allEntrances.Length, SourceMaps = checkedSources, MapsScanned = checkedMaps,
    FullScanMs = stopwatch.ElapsedMilliseconds, RecordedQuests = new[] { 65983, 65985, 66043, 65640 },
    Guard = guard, LotusGuard = lotusGuard, InnDoor = innDoor, AllEntrances = allEntrances,
    QuestActorLevels = targetLevels.Length, QuestActorMaps = targetMaps.Length,
    ReachableQuestActorMapsFromGridania = targetMaps.Length - unreachable.Length,
    AdditionalMapsWithPossibleTeleport = withTeleport.Length,
    AdditionalMapsViaVerifiedDutyEntrance = withDuty.Length,
    AdditionalMapsWithUniqueDutyFinder = withDutyFinder.Length, DutyFinderActuallyUnlockedChecked = false,
    UnmappedQuestActorMaps = stillUnmapped, TeleportsActuallyUnlockedChecked = false,
    NativeFfxivChecked = false }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
Console.WriteLine($"PASS: {checks} assertions, {allEntrances.Length} verified entrances in {checkedSources} source maps, {checkedMaps} maps scanned ({stopwatch.ElapsedMilliseconds} ms); four recorded quests and ordinary exits. Live game not tested.");

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
