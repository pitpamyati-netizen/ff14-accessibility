using System.Numerics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FF14Accessibility.Services;
using Navmesh;

if (args.Length != 2) throw new ArgumentException("HeightPathCheck <cache.navmesh> <report.json>");
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
Run(args[0], args[1]);

static void Run(string cache, string report)
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
    // Actual start and NPC coordinates from dalamud.log, 2026-10-01 18:27-18:32.
    var start = new Vector3(147.98969f, 10.776015f, -247.35329f);
    Vector3[] targets = [new(157.7019f, 15.9f, -270.34418f), new(147.08167f, 15.5f, -267.99426f)];
    var cases = new List<object>();
    foreach (var target in targets)
    {
        var samples = new List<object>();
        var native = query.PathfindMesh(start, target, true, true, 0, default).Select(w => w.Position).ToList();
        Console.WriteLine($"Native target {target}: {native.Count} points");
        HeightPath? planner = null;
        planner = new HeightPath(start, target, 2.5f, (p, xz, y) =>
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
        cases.Add(new { Start = start.ToString(), Target = target.ToString(), Passed = planner.Result != null,
            planner.Queries, planner.LastFailure, NativeRoute = native.Select(p => p.ToString()).ToArray(),
            Route = planner.Result?.Select(p => p.ToString()).ToArray(), Samples = samples });
        if (planner.Result == null || !HeightPath.ValidShape(planner.Result, start, planner.Result[^1])
            || Vector3.Distance(planner.Result[^1], target) > 2.5f
            || MathF.Abs(planner.Result[^1].Y - target.Y) > 1)
            throw new Exception($"Recorded NPC approach failed: {target}, {planner.LastFailure}");
        planner.Dispose();
    }
    File.WriteAllText(report, JsonSerializer.Serialize(new { Cache = Path.GetFileName(cache), Version = version,
        CacheSHA256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(cache))),
        Cases = cases, InGameVerified = false }, new JsonSerializerOptions { WriteIndented = true }));
}

public class QuietLog : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.ReturnType == typeof(void) ? null
        : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
}
