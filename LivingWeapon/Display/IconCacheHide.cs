using System;
using System.Collections.Generic;
using System.Linq;
using Holder = (long HolderAddr, long StringAddr, int Id, string Surface, LivingWeapon.NeedleKind Kind);

namespace LivingWeapon;

/// <summary>
/// LW-381 second half: hides a re-tiered icon's TWO drawn-icon cache records so the next menu
/// screen the player opens reloads it from modded.pac instead of showing the old art
/// (LIVE_LEDGER <c>[icon-cache-hide-reload]</c>, PROVEN 2026-09-01, owner live). Plain language:
/// once an icon has been drawn, the game remembers it in two places -- a "parts" record (name
/// plus a loaded-state flag) and a texture record (.tex path, byte size, a hash) -- and never
/// asks the file again while either stands. IconGlow.Pac.cs already writes the new glow into
/// modded.pac within seconds of a tier change; this class makes the NEXT screen open notice it.
///
/// COUPLING RULE (load-bearing, ledger-verified): hide both records or neither. Hiding only the
/// parts record makes the icon vanish, then come back STALE (its reload finds the still-resident
/// texture record and re-links to the OLD art). So a texture record hides FIRST, and a surface's
/// parts entries are only ever hidden once its own texture record actually did
/// (<see cref="IconCacheHidePolicy.DecideOutcome"/>).
///
/// A hide is a same-length in-place corruption (name pointer +1, id/hash zeroed) -- never a free
/// or a move; the game reclaims a hidden slot on its own (P6). Two full memory passes per
/// <see cref="Hide"/> call regardless of target count (D4; see <see cref="SweepStrings"/> and
/// <see cref="SweepPointers"/>, <see cref="ForEachChunk"/> for the in-battle abort) -- EXCEPT
/// pass 1 finding nothing at all (A3-13): nothing for pass 2 to look up, so it is skipped rather
/// than paying for a second full walk guaranteed to find no candidate. The writes themselves
/// (A3-12, IconCacheHide.Write.cs -- the real seam C10 split out once C3/C8 pushed this file past
/// 200 lines) are a handful of targeted reads/writes on already-resolved addresses, re-verified
/// before AND after -- cheap enough that the in-battle guard is deliberately NOT re-checked
/// there; only the two chunked sweeps re-check it. A4-1: this is a ruling, not a claim of
/// perfection -- every memory access goes through the guarded seam, the one thing that can still
/// throw is the first-call 4 MB reader allocation (<see cref="OutOfMemoryException"/>), and
/// both <c>Hide</c> call paths (the pac-flush hide and the pending-hide retry, both in
/// IconGlow.Hide.cs) catch <see cref="Exception"/> around their own call.
/// </summary>
internal sealed partial class IconCacheHide : IIconCacheHider
{
    private readonly IGameMemory _mem;
    private readonly Func<bool> _inBattle;
    private ChunkReader? _reader;   // D8: private, lazy -- never shared with Display's own sweep

    // Warn-once-per-launch: TextureMissing keys by id alone (D7); a refused write keys by
    // (id, surface), the granularity a live reader can actually act on.
    private readonly HashSet<int> _textureMissingWarnedOnce = new();
    private readonly HashSet<(int Id, string Surface)> _refusedWarnedOnce = new();
    // A4-3: a SEPARATE once-per-(id, surface)-per-launch set for a refused PARTS write -- this
    // never changes the surface's outcome (the coupling rule already succeeded once texHidden >
    // 0), so it is Debug, not Warn, and lives apart from _refusedWarnedOnce (that one is keyed to
    // the Refused outcome itself, i.e. no texture record could be written at all).
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
        if (stringMap.Count == 0)   // A3-13: nothing pass 2 could ever find -- skip its full sweep
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

    /// <summary>One fresh region snapshot per call (one Regions() call per pass, not per target);
    /// D3's "before starting and once per chunk" collapse into the one in-battle check below.</summary>
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

    /// <summary>PASS 1 -- per-chunk scan logic is pure, see <see cref="IconCacheHidePolicy.ScanChunkForStrings"/>.</summary>
    private bool SweepStrings(IReadOnlyDictionary<string, (int Id, string Surface, NeedleKind Kind)> lookup,
        out Dictionary<long, (int Id, string Surface, NeedleKind Kind)> stringMap)
    {
        var map = new Dictionary<long, (int, string, NeedleKind)>();
        bool ok = ForEachChunk((buf, lookback, searchable, bufBase) =>
            IconCacheHidePolicy.ScanChunkForStrings(buf, lookback, searchable, bufBase, lookup, map));
        stringMap = map;
        return ok;
    }

    /// <summary>PASS 2 -- per-chunk scan logic is pure, see <see cref="IconCacheHidePolicy.ScanChunkForPointers"/>.</summary>
    private bool SweepPointers(Dictionary<long, (int Id, string Surface, NeedleKind Kind)> stringMap,
        out List<Holder> holders)
    {
        var list = new List<Holder>();
        bool ok = ForEachChunk((buf, lookback, searchable, bufBase) =>
            IconCacheHidePolicy.ScanChunkForPointers(buf, lookback, searchable, bufBase, stringMap, list));
        holders = list;
        return ok;
    }

    /// <summary>Classification + the coupling rule for one target: texture records go first;
    /// parts are attempted only once a texture write actually succeeded.</summary>
    private void ClassifyAndWrite(IconCacheTarget t, List<Holder> holders, IconCacheHideResult result)
    {
        var parts = holders.Where(h => h.Id == t.Id && h.Surface == t.Surface && h.Kind == NeedleKind.PartsUri).ToList();
        var texes = holders.Where(h => h.Id == t.Id && h.Surface == t.Surface && h.Kind == NeedleKind.TexPath).ToList();

        int texHidden = 0;
        bool anyTexRefused = false;
        long refusedAddr = 0;
        string refusedReason = "";
        foreach (var h in texes)
        {
            if (!_mem.TryReadBytes(h.HolderAddr, IconCacheHidePolicy.TexHeadSize, out var head)) continue;
            uint hash = IconCacheHidePolicy.ClassifyTexRecord(head, t.Entry.Length);
            if (hash == 0) continue;
            if (TryHideTexRecord(h.HolderAddr, h.StringAddr, t.Entry.Length, hash, out var reason)) texHidden++;
            else
            {
                // A3-9: keep only the FIRST refusal's address/reason -- the only outcome that
                // ever surfaces it (Refused, see DecideOutcome) needs just the one to be useful.
                if (!anyTexRefused) { refusedAddr = h.HolderAddr; refusedReason = reason; }
                anyTexRefused = true;
            }
        }

        int partsHidden = 0;
        if (texHidden > 0)   // COUPLING: never touch a parts entry unless a texture actually hid
        {
            foreach (var h in parts)
            {
                if (!_mem.TryReadBytes(h.HolderAddr, IconCacheHidePolicy.PartsHeadSize, out var head)) continue;
                uint id = IconCacheHidePolicy.ClassifyPartsEntry(head);
                if (id == 0) continue;
                // A3-11: a lone parts-write refusal no longer downgrades this surface (texHidden >
                // 0 already satisfied the coupling rule) -- PartsHidden < PartsCandidates in the
                // Hidden summary line already shows the gap. A4-3: the reason is not discarded
                // though -- it is real information, so it surfaces on its own once-per-(id,
                // surface) Debug line instead.
                if (TryHidePartsEntry(h.HolderAddr, h.StringAddr, id, out var partsReason)) partsHidden++;
                else if (_partsRefusedWarnedOnce.Add((t.Id, t.Surface)))
                    ModLogger.Debug(LogVerb.Display,
                        IconCacheHidePolicy.PartsRefusedLine(t.Id, t.Surface, h.HolderAddr, partsReason));
            }
        }

        var outcome = IconCacheHidePolicy.DecideOutcome(parts.Count, texes.Count, texHidden, partsHidden, anyTexRefused);
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
