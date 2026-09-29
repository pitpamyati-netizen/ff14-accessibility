using System.Numerics;
using System.Reflection;
using System.Runtime.Loader;
using Dalamud.Plugin.Services;
using Navmesh;

var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher");
AssemblyLoadContext.Default.Resolving += (ctx, name) => {
    foreach (var dir in new[] {"devPlugins/vnavmesh", "addon/Hooks/dev"}) {
        var path = Path.Combine(root, dir, name.Name + ".dll");
        if (File.Exists(path)) return ctx.LoadFromAssemblyPath(path);
    }
    return null;
};
Run(root);

static void Run(string root) {
    var service = typeof(NavmeshQuery).Assembly.GetType("Navmesh.Service")!;
    var log = DispatchProxy.Create<IPluginLog, QuietLog>();
    service.GetProperty("Log", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, log);
    var from = new Vector3(58.22726f, 1.0359972f, 262.0598f);
    var to = new Vector3(67.063354f, .9387206f, 255.6344f);
    var avoid = new Vector3(58.25f, 1.75f, 261.5f);
    var passed = 0;
    foreach (var path in Directory.GetFiles(Path.Combine(root, "pluginConfigs/vnavmesh/meshcache"), "*w1f2*.navmesh")) {
        using var reader = new BinaryReader(File.OpenRead(path));
        reader.ReadUInt32(); reader.ReadUInt32(); var version = reader.ReadInt32();
        reader.BaseStream.Position = 0;
        var mesh = Navmesh.Navmesh.Deserialize(reader, version);
        var q = new NavmeshQuery(mesh);
        if (q.FindNearestMeshPoly(from, .5f, 1) == 0 || q.FindNearestMeshPoly(to, .5f, 1) == 0) continue;
        Console.WriteLine($"Cache={Path.GetFileName(path)} version={version}");
        foreach (var t in typeof(NavmeshCustomization).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(NavmeshCustomization)))) {
            if (t.GetCustomAttributes<CustomizationTerritoryAttribute>().Any(a => a.TerritoryID == 141)) {
                var c = (NavmeshCustomization)Activator.CreateInstance(t)!;
                c.CustomizeMesh(mesh, []); Console.WriteLine($"Customization={t.Name} version={c.Version}");
            }
        }
        foreach (var radius in new[] {0f, .25f, .4f, .5f, .8f}) {
            var route = q.PathfindMesh(from, to, false, true, 0, default,
                radius == 0 ? null : new NavmeshQuery.AvoidRadiusFilter(avoid, radius));
            Console.WriteLine($"Radius={radius} count={route.Count} path=" + string.Join(" -> ", route.Select(p => p.Position.ToString())));
        }
        Console.WriteLine("Centers=" + string.Join(" -> ", q.PathfindMesh(from, to, false, false, 0, default).Select(p => p.Position.ToString())));
        var alternatives = new List<(float Length, Vector3 Via, List<Vector3> Route)>();
        foreach (var radius in new[] {1f, 2f, 3f, 5f, 8f}) {
            for (int i = 0; i < 16; ++i) {
                var probe = from + new Vector3(MathF.Cos(i*MathF.PI/8)*radius, 0, MathF.Sin(i*MathF.PI/8)*radius);
                var via = q.FindNearestPointOnMesh(probe, .5f, 1f);
                if (via == null) continue;
                var a = q.PathfindMesh(from, via.Value, true, true, 0, default);
                if (q.LastPath.Count == 0 || q.LastPath[^1] != q.FindNearestMeshPoly(via.Value)) continue;
                var b = q.PathfindMesh(via.Value, to, true, true, 0, default);
                if (q.LastPath.Count == 0 || q.LastPath[^1] != q.FindNearestMeshPoly(to)) continue;
                var all = a.Concat(b).Select(w=>w.Position).ToList();
                if (Enumerable.Range(1, all.Count-1).Any(k=>SegmentNear(all[k-1], all[k], avoid, .4f))) continue;
                var length = Enumerable.Range(1,all.Count-1).Sum(k=>Vector3.Distance(all[k-1],all[k]));
                alternatives.Add((length,via.Value,all));
            }
        }
        Console.WriteLine($"Alternative count={alternatives.Count}");
        foreach (var alt in alternatives.OrderBy(a=>a.Length).Take(3)) Console.WriteLine($"via={alt.Via} length={alt.Length} path=" + string.Join(" -> ",alt.Route));
        var native = q.PathfindMesh(from, to, true, true, 0, default).Select(p=>p.Position).ToList();
        native.RemoveAt(0); // vnavmesh already pruned the start point in the recorded stall
        if (!FF14Accessibility.Services.GroundDetour.TryChoose(from, native, out var corner, out var rejoin))
            throw new Exception("Recorded stalled path was not selected for recovery.");
        using var planner = new FF14Accessibility.Services.GroundDetour(from,corner,rejoin,
            p=>q.FindNearestPointOnMesh(p,.5f,.5f),
            (a,b,ct)=>Task.FromResult(q.PathfindMesh(a,b,true,true,0,ct).Select(p=>p.Position).ToList()));
        for (int tick=0;tick<100 && !planner.Done;++tick) planner.Update();
        if (planner.Result == null) throw new Exception("Production planner did not find the recorded corner detour.");
        ++passed; Console.WriteLine("PRODUCTION PASS " + string.Join(" -> ",planner.Result));
    }
    if (passed == 0) throw new Exception("No recorded cache replay ran.");
    Console.WriteLine($"Passed {passed} recorded cache replays. No live game movement tested.");
}

static bool SegmentNear(Vector3 a, Vector3 b, Vector3 center, float radius) {
    var p = new Vector2(a.X,a.Z); var d = new Vector2(b.X-a.X,b.Z-a.Z); var c = new Vector2(center.X,center.Z);
    var t = d.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(c-p,d)/d.LengthSquared(),0,1):0;
    return Vector2.Distance(p+d*t,c)<radius;
}

public class QuietLog : DispatchProxy {
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.ReturnType == typeof(void) ? null :
        targetMethod.ReturnType.IsValueType ? Activator.CreateInstance(targetMethod.ReturnType) : null;
}
