using System;
using System.Collections.Generic;
using System.Linq;
using Holder = (long HolderAddr, long StringAddr, int Id, string Surface, LivingWeapon.NeedleKind Kind);

namespace LivingWeapon;

/// <summary>
/// Hides a re-tiered icon's two drawn-icon cache records (parts and texture).
/// The next menu screen then reloads it from modded.pac (written by IconGlow.Pac.cs). LIVE_LEDGER <c>[icon-cache-hide-reload]</c>, proven 2026-09-01.
/// Hide both records or neither. Parts alone makes the icon vanish and return stale, so the texture hides first and
/// parts only after it did (<see cref="IconCacheHidePolicy.DecideOutcome"/>). A hide is a same-length in-place edit (name pointer +1, id/hash zeroed).
/// <see cref="Hide"/> makes two passes (<see cref="SweepStrings"/>, <see cref="SweepPointers"/>; <see cref="ForEachChunk"/> aborts in battle) and skips pass 2 if pass 1 finds nothing.
/// Writes are in IconCacheHide.Write.cs. Only the first-call 4 MB reader allocation (<see cref="OutOfMemoryException"/>) can throw; both callers in IconGlow.Hide.cs catch it.
/// </summary>
internal sealed partial class IconCacheHide : IIconCacheHider
{
    private readonly IGameMemory _mem;
    private readonly Func<bool> _inBattle;
    private ChunkReader? _reader;   // Private, lazy -- never shared with Display's own sweep

    // Warn once per launch. TextureMissing keys by id, a refused write by (id, surface).
    private readonly HashSet<int> _textureMissingWarnedOnce = new();
    private readonly HashSet<(int Id, string Surface)> _refusedWarnedOnce = new();
    // Once per (id, surface) per launch for a refused parts write. Debug only, since the surface
    // already succeeded once a texture hid.
    private readonly HashSet<(int Id, string Surface)> _partsRefusedWarnedOnce = new();

    internal IconCacheHide(IGameMemory mem, Func<bool> inBattle)
    {
        _mem = mem;
        _inBattle = inBattle;
    }

    public IconCacheHideResult Hide(IReadOnlyList<IconCacheTarget> targets)
    {
        var result = new IconCacheHideResult();
        if (targets.Count == 0) return result;
        _reader ??= new ChunkReader(_mem);

        var lookup = IconCacheHidePolicy.BuildNeedleLookup(targets);
        if (!SweepStrings(lookup, out var stringMap))
        {
            foreach (var t in targets) Report(t, new SurfaceOutcome(HideOutcome.Postponed, 0, 0, 0, 0), result);
            return result;
        }
        if (stringMap.Count == 0)   // Nothing pass 2 could ever find -- skip its full sweep
        {
            foreach (var t in targets) Report(t, new SurfaceOutcome(HideOutcome.NothingResident, 0, 0, 0, 0), result);
            return result;
        }
        if (!SweepPointers(stringMap, out var holders))
        {
            foreach (var t in targets) Report(t, new SurfaceOutcome(HideOutcome.Postponed, 0, 0, 0, 0), result);
            return result;
        }

        foreach (var t in targets) ClassifyAndWrite(t, holders, result);
        return result;
    }

    /// <summary>Takes one region snapshot per call and stops at the first chunk where the game is in battle.</summary>
    private bool ForEachChunk(Action<byte[], int, int, long> onChunk)
    {
        _reader!.Snapshot();
        foreach (var (rbase, rsize) in _reader.Regions)
        {
            long rend = rbase + rsize;
            for (long chunkStart = rbase; chunkStart < rend; chunkStart += ChunkReader.ChunkSize)
            {
                if (_inBattle()) return false;
                int read = _reader.ReadInRegion(chunkStart, rbase, rend, out int lookback, out int searchable);
                if (read == 0) continue;
                onChunk(_reader.Buf, lookback, searchable, chunkStart - lookback);
            }
        }
        return true;
    }

    /// <summary>Pass 1. The per-chunk scan is pure, see <see cref="IconCacheHidePolicy.ScanChunkForStrings"/>.</summary>
    private bool SweepStrings(IReadOnlyDictionary<string, (int Id, string Surface, NeedleKind Kind)> lookup,
        out Dictionary<long, (int Id, string Surface, NeedleKind Kind)> stringMap)
    {
        var map = new Dictionary<long, (int, string, NeedleKind)>();
        bool ok = ForEachChunk((buf, lookback, searchable, bufBase) =>
            IconCacheHidePolicy.ScanChunkForStrings(buf, lookback, searchable, bufBase, lookup, map));
        stringMap = map;
        return ok;
    }

    /// <summary>Pass 2. The per-chunk scan is pure, see <see cref="IconCacheHidePolicy.ScanChunkForPointers"/>.</summary>
    private bool SweepPointers(Dictionary<long, (int Id, string Surface, NeedleKind Kind)> stringMap,
        out List<Holder> holders)
    {
        var list = new List<Holder>();
        bool ok = ForEachChunk((buf, lookback, searchable, bufBase) =>
            IconCacheHidePolicy.ScanChunkForPointers(buf, lookback, searchable, bufBase, stringMap, list));
        holders = list;
        return ok;
    }

    /// <summary>Hides one target's texture records first, then its parts entries only if a texture hid.</summary>
    private void ClassifyAndWrite(IconCacheTarget t, List<Holder> holders, IconCacheHideResult result)
    {
        var parts = holders.Where(h => h.Id == t.Id && h.Surface == t.Surface && h.Kind == NeedleKind.PartsUri).ToList();
        var texes = holders.Where(h => h.Id == t.Id && h.Surface == t.Surface && h.Kind == NeedleKind.TexPath).ToList();

        int texHidden = 0, liveTex = 0;
        bool anyTexRefused = false;
        long refusedAddr = 0;
        string refusedReason = "";
        foreach (var h in texes)
        {
            if (!_mem.TryReadBytes(h.HolderAddr, IconCacheHidePolicy.TexHeadSize, out var head)) continue;
            uint hash = IconCacheHidePolicy.ClassifyTexRecord(head, t.Entry.Length);
            if (hash == 0) continue;
            liveTex++;
            if (TryHideTexRecord(h.HolderAddr, h.StringAddr, t.Entry.Length, hash, out var reason)) texHidden++;
            else
            {
                // Keep only the first refusal's address and reason, which is all Refused reports.
                if (!anyTexRefused) { refusedAddr = h.HolderAddr; refusedReason = reason; }
                anyTexRefused = true;
            }
        }

        int partsHidden = 0;
        if (texHidden > 0 || (liveTex == 0 && !anyTexRefused))
        {
            foreach (var h in parts)
            {
                if (!_mem.TryReadBytes(h.HolderAddr, IconCacheHidePolicy.PartsHeadSize, out var head)) continue;
                uint id = IconCacheHidePolicy.ClassifyPartsEntry(head);
                if (id == 0) continue;
                // A lone parts refusal does not downgrade the surface (a texture already hid). The gap shows as
                // PartsHidden < PartsCandidates in the summary line, and the reason goes to a once-per-surface Debug line.
                if (TryHidePartsEntry(h.HolderAddr, h.StringAddr, id, out var partsReason)) partsHidden++;
                else if (_partsRefusedWarnedOnce.Add((t.Id, t.Surface)))
                    ModLogger.Debug(LogVerb.Display,
                        IconCacheHidePolicy.PartsRefusedLine(t.Id, t.Surface, h.HolderAddr, partsReason));
            }
        }

        var outcome = IconCacheHidePolicy.DecideOutcome(parts.Count, texes.Count, liveTex, texHidden, partsHidden, anyTexRefused);
        Report(t, outcome, result, refusedAddr, refusedReason);
    }

    private void Report(IconCacheTarget t, SurfaceOutcome outcome, IconCacheHideResult result,
        long refusedAddr = 0, string? refusedReason = null)
    {
        result.Set(t.Id, t.Surface, outcome);
        string line = IconCacheHidePolicy.SummaryLine(t.Id, t.Surface, outcome, refusedAddr, refusedReason);
        bool warn = outcome.Outcome switch
        {
            HideOutcome.TextureMissing => outcome.PartsCandidates > 0 && _textureMissingWarnedOnce.Add(t.Id),
            HideOutcome.Refused => _refusedWarnedOnce.Add((t.Id, t.Surface)),
            _ => false,
        };
        if (warn) ModLogger.Warn(LogVerb.Display, line);
        else ModLogger.Debug(LogVerb.Display, line);
    }
}
