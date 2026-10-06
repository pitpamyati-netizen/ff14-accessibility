using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace FF14Accessibility.Services;

/// <summary>An interaction entrance, not a walk-through zone border.</summary>
public sealed record InteriorEntrance(uint SourceMapId, uint TargetMapId, uint BaseId,
    byte LevelType, Vector3 Position);

/// <summary>
/// Adds entrances absent from MapMarker. EObj -> Warp -> PopRange is an explicit
/// game link; PopRange is the ARRIVAL and must never be used as the entrance.
/// NPC scripted travel has no generic Warp link. Only the measured First Bow
/// guard is included, with its current Warp/DefaultTalk/Level links checked.
/// Native interaction and the game's conditions still decide whether to enter.
/// </summary>
internal sealed class InteriorEntranceService(IDataManager data, IPluginLog log)
{
    private List<InteriorEntrance>? _all;
    public IReadOnlyList<InteriorEntrance> All => _all ??= Build();

    private List<InteriorEntrance> Build()
    {
        var result = new List<InteriorEntrance>();
        var links = new Dictionary<uint, Warp>();
        foreach (var obj in data.GetExcelSheet<EObj>())
            if (obj.Data.TryGetValue<Warp>(out var warp)) links[obj.RowId] = warp;

        foreach (var level in data.GetExcelSheet<Level>())
            if (level.Type == 45 && links.TryGetValue(level.Object.RowId, out var warp))
                Add(level, warp);

        // Verified against quest 65983 and installed 2026.09.15 game tables:
        // Gods' Quiver bow (1000423), New Gridania -> Seat of the First Bow.
        // His DefaultTalk 590353 is Warp 131074's refusal event; the quest
        // listener names this same guard. No nearest-NPC or name guessing.
        if (data.GetExcelSheet<Warp>().TryGetRow(131074, out var firstBow)
            && firstBow.TerritoryType.RowId == 204
            && firstBow.ConditionFailEvent.RowId == 590353
            && data.GetExcelSheet<ENpcBase>().TryGetRow(1000423, out var guard)
            && guard.ENpcData.Any(d => d.Is<DefaultTalk>() && d.RowId == 590353)
            && data.GetExcelSheet<Level>().TryGetRow(3861082, out var guardLevel)
            && guardLevel.Type == 8 && guardLevel.Object.RowId == guard.RowId
            && guardLevel.Territory.RowId == 132 && guardLevel.Map.RowId == 2)
            Add(guardLevel, firstBow);
        else
            log.Warning("[Orte] First Bow entrance links changed; refusing an unverified guard route.");

        log.Info($"[Orte] {result.Count} interaction entrances with verified Warp and Level links.");
        return result;

        void Add(Level level, Warp warp)
        {
            var arrival = warp.PopRange.ValueNullable;
            if (arrival == null) return;
            var entrance = Validate(level.Map.RowId, level.Territory.RowId, level.Type, level.Object.RowId,
                new(level.X, level.Y, level.Z), warp.TerritoryType.RowId,
                arrival.Value.Map.RowId, arrival.Value.Territory.RowId,
                level.Map.ValueNullable?.TerritoryType.RowId ?? 0,
                arrival.Value.Map.ValueNullable?.TerritoryType.RowId ?? 0);
            if (entrance != null && !result.Contains(entrance)) result.Add(entrance);
        }
    }

    internal static InteriorEntrance? Validate(uint sourceMap, uint sourceTerritory, byte type, uint baseId,
        Vector3 position, uint warpTerritory, uint targetMap, uint arrivalTerritory,
        uint sourceMapTerritory, uint targetMapTerritory)
    {
        if (sourceMap == 0 || targetMap == 0 || sourceMap == targetMap || baseId == 0
            || type is not (8 or 45) || sourceTerritory == 0 || warpTerritory == 0
            || sourceTerritory != sourceMapTerritory || sourceTerritory == warpTerritory
            || arrivalTerritory != warpTerritory || targetMapTerritory != warpTerritory
            || !float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            return null;
        return new(sourceMap, targetMap, baseId, type, position);
    }
}
