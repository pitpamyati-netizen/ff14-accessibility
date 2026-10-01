using System.Numerics;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using Lumina;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;

// Offline intersection with regular BG/terrain PCB triangles. Analytic boxes,
// conditional layers and live door states are not reproduced. No game memory
// or caches are changed; no intersection does not certify live clearance.
internal sealed class GuildGeometryProbe
{
    private readonly List<(string Name, Vector3 A, Vector3 B, Vector3 C, ulong Material)> _triangles = [];

    internal unsafe GuildGeometryProbe(string sqpack)
    {
        using var game = new GameData(sqpack);
        var layout = game.GetFile<LgbFile>("bg/ffxiv/fst_f1/twn/f1t2/level/bg.lgb")!;
        foreach (var layer in layout.Layers)
        foreach (var instance in layer.InstanceObjects)
        {
            if (instance.AssetType != LayerEntryType.BG) continue;
            var bg = (LayerCommon.BGInstanceObject)instance.Object;
            if (string.IsNullOrEmpty(bg.CollisionAssetPath) || layer.Name?.StartsWith("bg_") == true) continue;
            var t = instance.Transform;
            var world = Matrix4x4.CreateScale(t.Scale.X, t.Scale.Y, t.Scale.Z)
                * Matrix4x4.CreateRotationX(t.Rotation.X) * Matrix4x4.CreateRotationY(t.Rotation.Y)
                * Matrix4x4.CreateRotationZ(t.Rotation.Z)
                * Matrix4x4.CreateTranslation(t.Translation.X, t.Translation.Y, t.Translation.Z);
            var file = game.GetFile<Lumina.Data.FileResource>(bg.CollisionAssetPath)!;
            fixed (byte* raw = file.Data)
            {
                var header = (MeshPCB.FileHeader*)raw;
                if (header->Version is not (1 or 4)) throw new Exception("Unsupported PCB version");
                Add((MeshPCB.FileNode*)(header + 1), world, bg.CollisionAssetPath);
            }
        }
        foreach (var terrain in new[] { "bg/ffxiv/fst_f1/twn/f1t2/collision", "bg/ffxiv/fst_f1/twn/f1t2/bg/collision", "bg/ffxiv/fst_f1/twn/f1t2/terrain/collision" })
        {
            var list = game.GetFile<Lumina.Data.FileResource>(terrain + "/list.pcb");
            if (list == null) continue;
            fixed (byte* raw = list.Data)
            {
                var header = (ColliderStreamed.FileHeader*)raw;
                foreach (ref var entry in new Span<ColliderStreamed.FileEntry>(header + 1, header->NumMeshes))
                {
                    var name = $"{terrain}/tr{entry.MeshId:d4}.pcb";
                    var file = game.GetFile<Lumina.Data.FileResource>(name)!;
                    fixed (byte* mesh = file.Data) Add((MeshPCB.FileNode*)((MeshPCB.FileHeader*)mesh + 1), Matrix4x4.Identity, name);
                }
            }
        }
        if (_triangles.Count == 0) throw new Exception("No static geometry read");
        Console.WriteLine($"Guild geometry: {_triangles.Count} triangles, materials " + string.Join(",", _triangles.Select(t => t.Material.ToString("X")).Distinct()));
    }

    private unsafe void Add(MeshPCB.FileNode* node, Matrix4x4 world, string name)
    {
        if (node == null) return;
        foreach (ref var p in node->Primitives)
            _triangles.Add((name, Vector3.Transform(node->Vertex(p.V1), world),
                Vector3.Transform(node->Vertex(p.V2), world), Vector3.Transform(node->Vertex(p.V3), world), p.Material));
        Add(node->Child1, world, name); Add(node->Child2, world, name);
    }

    internal object? FirstHit(Vector3 start, IReadOnlyList<Vector3> route)
    {
        var from = start;
        foreach (var to in route)
        {
            var delta = to - from;
            var length = delta.Length();
            if (length >= 0.05f)
            foreach (var height in new[] { 0.8f, 1.2f })
            foreach (var triangle in _triangles)
            {
                if ((triangle.Material & 0x4000) == 0) continue;
                var direction = delta / length;
                var origin = from + new Vector3(0, height, 0);
                if (Intersect(origin, direction, length, triangle.A, triangle.B, triangle.C, out var distance))
                    return new { triangle.Name, From = from.ToString(), To = to.ToString(), Height = height,
                        Point = (origin + direction * distance).ToString(), triangle.Material,
                        DistanceFromPlayer = Vector3.Distance(start, origin + direction * distance) };
            }
            from = to;
        }
        return null;
    }

    private static bool Intersect(Vector3 origin, Vector3 direction, float length, Vector3 a, Vector3 b, Vector3 c, out float distance)
    {
        distance = 0;
        var e1 = b - a; var e2 = c - a;
        var p = Vector3.Cross(direction, e2); var det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-6f) return false;
        var t = origin - a; var u = Vector3.Dot(t, p) / det;
        if (u < 0 || u > 1) return false;
        var q = Vector3.Cross(t, e1); var v = Vector3.Dot(direction, q) / det;
        if (v < 0 || u + v > 1) return false;
        distance = Vector3.Dot(e2, q) / det;
        return distance >= 0 && distance <= length;
    }
}
