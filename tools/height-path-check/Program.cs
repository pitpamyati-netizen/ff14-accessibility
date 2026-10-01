using System.Numerics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FF14Accessibility.Services;
using Navmesh;

if (args.Length is not (2 or 3)) throw new ArgumentException("HeightPathCheck <cache.navmesh> <report.json> [sqpack]");
var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher");
AssemblyLoadContext.Default.Resolving += (ctx, name) =>
{
    foreach (var dir in new[] { "devPlugins/vnavmesh", "addon/Hooks/dev" })
    {
        var path = Path.Combine(root, dir, name.Name + ".dll");
        if (File.Exists(path)) return ctx.LoadFromAssemblyPath(path);
    }
    return null;
};
Run(args[0], args[1], args.Length == 3 ? args[2] : null);

static void Run(string cache, string report, string? sqpack)
{
    typeof(NavmeshQuery).Assembly.GetType("Navmesh.Service")!
        .GetProperty("Log", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
        .SetValue(null, DispatchProxy.Create<IPluginLog, QuietLog>());
    using var reader = new BinaryReader(File.OpenRead(cache));
    reader.ReadUInt32(); reader.ReadUInt32(); var version = reader.ReadInt32();
    reader.BaseStream.Position = 0;
    var mesh = Navmesh.Navmesh.Deserialize(reader, version);
    foreach (var type in typeof(NavmeshCustomization).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(NavmeshCustomization))))
        if (type.GetCustomAttributes<CustomizationTerritoryAttribute>().Any(a => a.TerritoryID == 133))
        {
            var customization = (NavmeshCustomization)Activator.CreateInstance(type)!;
            customization.CustomizeMesh(mesh, [54]);
            Console.WriteLine($"Customization={type.Name}, version={customization.Version}");
        }
    var query = new NavmeshQuery(mesh);
    var geometry = sqpack == null ? null : new GuildGeometryProbe(sqpack);
    // Actual start and NPC coordinates from dalamud.log, 2026-10-01 18:27-18:32.
    var start = new Vector3(147.98969f, 10.776015f, -247.35329f);
    Vector3[] targets = [new(157.7019f, 15.9f, -270.34418f), new(147.08167f, 15.5f, -267.99426f)];
    var cases = new List<object>();
    var routes = targets.Select(target => (Name: "guild NPC approach", Start: start, Target: target, Range: 2.5f, ExpectedPass: true)).ToList();
    // Exit attempts from the actual 6.08.88 log, 2026-10-01 20:55-20:57.
    foreach (var origin in new[] { new Vector3(156.6f, 15.9f, -270.6f), new Vector3(163.54092f, 15.900455f, -272.5384f) })
        routes.Add(("guild exit toward New Gridania", origin, new(-44.2f, 7.2f, -136.7f), 1f, true));
    var guild = new Vector3(163.54092f, 15.900455f, -272.5384f);
    routes.Add(("west pier wrong roof must remain rejected", guild, new(175.75f, 7.5f, -230.25f), 1f, false));
    var marker = new Vector3(182.5f, 15.9f, -240.5f);
    var pier = query.FindNearestPointOnMesh(marker, 2, 100, false)
        ?? throw new Exception("No mesh in west pier marker column");
    routes.Add(("west pier corrected marker column", guild, pier, 1f, true));
    var failed = 0;
    foreach (var route in routes)
    {
        start = route.Start;
        var target = route.Target;
        var samples = new List<object>();
        var native = query.PathfindMesh(start, target, true, true, 0, default).Select(w => w.Position).ToList();
        Console.WriteLine($"Native target {target}: {native.Count} points");
        HeightPath? planner = null;
        planner = new HeightPath(start, target, route.Range, (p, xz, y) =>
        {
            var floor = query.FindNearestPointOnMesh(p, xz, y);
            samples.Add(new { Point = p.ToString(), XZ = xz, Y = y, Floor = floor?.ToString() });
            return floor;
        }, (a, b, ct) =>
        {
            return Task.FromResult(query.PathfindMesh(a, b, true, true, 0, ct).Select(w => w.Position).ToList());
        });
        for (var tick = 0; tick < 2000 && !planner.Done; ++tick) planner.Update();
        Console.WriteLine($"Result={planner.Result != null}, queries={planner.Queries}, failure={planner.LastFailure}");
        var staticHit = planner.Result == null ? null : geometry?.FirstHit(start, planner.Result);
        if (staticHit != null) Console.WriteLine("Static PCB intersection: " + JsonSerializer.Serialize(staticHit));
        var supported = planner.Result != null && HeightPath.ValidShape(planner.Result, start, planner.Result[^1])
            && Vector3.Distance(planner.Result[^1], target) <= route.Range
            && MathF.Abs(planner.Result[^1].Y - target.Y) <= 1;
        cases.Add(new { route.Name, Start = start.ToString(), Target = target.ToString(), route.ExpectedPass,
            Passed = supported == route.ExpectedPass, RouteSupported = supported,
            planner.Queries, planner.LastFailure, StaticGeometryHit = staticHit, NativeRoute = native.Select(p => p.ToString()).ToArray(),
            Route = planner.Result?.Select(p => p.ToString()).ToArray(), Samples = samples });
        if (supported != route.ExpectedPass) ++failed;
        planner.Dispose();
    }
    var adaptiveCases = new List<object>();
    // The new graph must also work with the installed provider's actual paths,
    // polygon centres and partial endpoints, not just synthetic test surfaces.
    foreach (var target in new[] { targets[0], new Vector3(232.8f, -4.5f, -238.1f) })
    {
        var reachesNpc = target == targets[0];
        using var adaptive = new AdaptiveGroundPath(guild, target, reachesNpc ? 1 : 6,
            (p, xz, y) => query.FindNearestPointOnMesh(p, xz, y),
            (a, b, ct) => Task.FromResult(query.PathfindMesh(a, b, true, true, 0, ct).Select(w => w.Position).ToList()),
            (_, _) => true);
        for (var tick = 0; tick < 30000 && !adaptive.Done; ++tick) adaptive.Update();
        var valid = adaptive.Done && adaptive.Result != null && adaptive.Result.All(l => !l.Jump
            && HeightPath.ValidShape(l.Points, l.From, l.End));
        // The zone edge is outside the continuous mesh: a validated approach is
        // useful progress, and must stay labelled as an approach, not arrival.
        var ok = valid && (reachesNpc ? !adaptive.ApproachOnly
            && Vector3.Distance(adaptive.Result![^1].End, target) <= 1 : adaptive.ApproachOnly);
        adaptiveCases.Add(new { Start = guild.ToString(), Target = target.ToString(), Passed = ok,
            adaptive.ApproachOnly, adaptive.Expanded, adaptive.Queries, adaptive.LastFailure,
            Legs = adaptive.Result?.Select(l => new { l.Jump, From = l.From.ToString(), End = l.End.ToString(), l.Cost }).ToArray() });
        Console.WriteLine($"Adaptive: passed={ok}, approachOnly={adaptive.ApproachOnly}, nodes={adaptive.Expanded}, queries={adaptive.Queries}");
        if (!ok) ++failed;
    }
    File.WriteAllText(report, JsonSerializer.Serialize(new { Cache = Path.GetFileName(cache), Version = version,
        CacheSHA256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(cache))),
        Cases = cases, AdaptiveCases = adaptiveCases, InGameVerified = false, CollisionSceneVerified = false }, new JsonSerializerOptions { WriteIndented = true }));
    if (failed != 0) throw new Exception($"Recorded guild routes failed: {failed}/{routes.Count}");
}

public class QuietLog : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.ReturnType == typeof(void) ? null
        : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
}
