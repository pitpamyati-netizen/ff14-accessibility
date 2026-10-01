using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FF14Accessibility.Services;
using DotRecast.Detour;
using Navmesh;

if (args.Length != 2) throw new ArgumentException("NavigationMeshCheck <cache directory> <report.json>");
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

static void Run(string directory, string report)
{
    typeof(NavmeshQuery).Assembly.GetType("Navmesh.Service")!
        .GetProperty("Log", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
        .SetValue(null, DispatchProxy.Create<IPluginLog, QuietLog>());
    var cases = new List<object>();
    var caches = new List<object>();
    var passed = 0; var total = 0;
    // Reproducible coordinates chosen from actual mesh polygons, independent
    // of the planner's candidate rings. No movement or writes to game caches.
    foreach (var file in Directory.GetFiles(directory, "*.navmesh").Order())
    {
        if (Path.GetFileName(file).StartsWith("__", StringComparison.Ordinal)) continue;
        using var reader = new BinaryReader(File.OpenRead(file));
        reader.ReadUInt32(); reader.ReadUInt32(); var version = reader.ReadInt32();
        reader.BaseStream.Position = 0;
        var mesh = Navmesh.Navmesh.Deserialize(reader, version);
        caches.Add(new { File = Path.GetFileName(file), Version = version,
            SHA256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))) });
        var query = new NavmeshQuery(mesh);
        var refs = new HashSet<long>();
        for (var tileIndex = 0; tileIndex < mesh.Mesh.GetMaxTiles(); ++tileIndex)
        {
            var tile = mesh.Mesh.GetTile(tileIndex);
            if (tile?.data?.header == null) continue;
            var baseRef = mesh.Mesh.GetPolyRefBase(tile);
            for (var i = 0; i < tile.data.header.polyCount; ++i) refs.Add(baseRef | (long)(uint)i);
        }
        var components = new List<HashSet<long>>();
        while (refs.Count != 0)
        {
            var component = query.FindReachableMeshPolys(refs.First());
            refs.ExceptWith(component); components.Add(component);
        }
        if (components.Count == 0) continue;
        var points = components.MaxBy(c => c.Count)!.Order().Select(r =>
        {
            var p = mesh.Mesh.GetPolyCenter(r);
            return new Vector3(p.X, p.Y, p.Z);
        }).ToList();
        if (points.Count < 10) continue;
        var from = points[points.Count / 2];
        var targets = new[] { points.MinBy(p => p.X), points.MaxBy(p => p.X),
            points.MinBy(p => p.Z), points.MaxBy(p => p.Z), points.MaxBy(p => p.Y), points.MinBy(p => p.Y) }.Distinct();
        foreach (var goal in targets)
        {
            if (Vector3.Distance(from, goal) < 3) continue;
            using var plan = new HeightPath(from, goal, 2.5f,
                (p, xz, y) => query.FindNearestPointOnMesh(p, xz, y),
                (a, b, ct) => Task.FromResult(query.PathfindMesh(a, b, true, true, 0, ct).Select(w => w.Position).ToList()));
            for (var tick = 0; tick < 3000 && !plan.Done; ++tick) plan.Update();
            var ok = plan.Done && plan.Result != null && HeightPath.ValidShape(plan.Result, from, plan.Result[^1])
                && Vector3.Distance(plan.Result[^1], goal) <= 2.5f;
            ++total; if (ok) ++passed;
            cases.Add(new { Cache = Path.GetFileName(file), Start = from.ToString(), Goal = goal.ToString(),
                Passed = ok, plan.Queries, plan.LastFailure, Points = plan.Result?.Count ?? 0 });
            Console.WriteLine($"{Path.GetFileName(file)}: {ok}, queries={plan.Queries}, {plan.LastFailure}");
        }
    }
    File.WriteAllText(report, JsonSerializer.Serialize(new { Total = total, Passed = passed, Caches = caches, Cases = cases,
        InGameVerified = false, CollisionSceneVerified = false, CustomizationsApplied = false }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Passed {passed}/{total}; raw cached ground only, no live collision or customization links.");
    if (passed != total || total == 0) Environment.ExitCode = 1;
}

public class QuietLog : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.ReturnType == typeof(void) ? null
        : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
}
