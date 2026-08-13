using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Bulwark's terrain read/write half: PLANT (the initial write or deferred-occupied parking of
/// the ONE behind tile), per-tick MAINTAIN (re-assert + the deferred vacancy watch), RELEASE
/// (exact-byte restore), and the shared occupancy scan. Bulwark.cs is the trigger/edge-tracking
/// half (ctor, gate chain, the turn-flag machinery that calls into these methods). See
/// Bulwark.cs's class doc for the full mechanism provenance.
/// </summary>
internal sealed partial class Bulwark
{
    /// <summary>The behind tile while it is occupied (at plant time or emptied and re-filled
    /// since): watched for two consecutive vacant ticks before being raised (AC criterion 6), and
    /// also used to park a VACANT tile that was transiently unreadable/unwritable at plant time
    /// (the same watch self-heals a transient memory miss). Mutable by design -- the per-tick
    /// watch just advances/resets this in place. At most one entry exists at a time (a single
    /// behind tile), but keyed by idx to reuse the same dictionary machinery.</summary>
    private sealed class DeferredTile
    {
        public readonly int X, Y;
        public int VacantStreak;
        public DeferredTile(int x, int y) { X = x; Y = y; VacantStreak = 0; }
    }

    private static string FacingLetter(int facing) => facing switch
    {
        0 => "S",
        1 => "W",
        2 => "N",
        3 => "E",
        _ => "?",
    };

    /// <summary>PLANT: read the wielder's tile, layer, and facing off ONE byte read
    /// (Offsets.ALayerBit packs both -- see Offsets.cs), sanity-gate the map dims (AC A3), then
    /// resolve the single tile directly behind the wielder (BulwarkPolicy.BehindTile) and either
    /// write the obstacle bit (vacant, readable, writable) or park it for the deferred vacancy
    /// watch (occupied, OR vacant but transiently unreadable/unwritable). Off-map behind (the
    /// wielder's back is to the map edge) plants NOTHING -- no toast, no state change. One toast
    /// otherwise, regardless of whether the tile was written immediately or deferred -- the stance
    /// itself is what the announcement is for.</summary>
    private void Plant(long entry)
    {
        int raw = _mem.U8(entry + Offsets.ALayerBit);
        int layerBit = (raw >> 7) & 1;
        int facing = raw & 0x03;

        int gx = _mem.U8(entry + Offsets.AGx);
        int gy = _mem.U8(entry + Offsets.AGy);

        int w = _mem.U8(Offsets.MapDimsWH);
        int h = _mem.U8(Offsets.MapDimsWH + 1);
        if (!BulwarkPolicy.DimsSane(w, h, gx, gy))
        {
            ModLogger.Debug(LogVerb.Signature, $"Bulwark refused to plant: map dims read out of range (w={w} h={h} wielder at ({gx},{gy})).");
            return;
        }

        var behind = BulwarkPolicy.BehindTile(gx, gy, facing, w, h, layerBit);
        if (behind is null)
        {
            ModLogger.Debug(LogVerb.Signature, $"Bulwark refused to plant: the wielder's back is to the map edge at ({gx},{gy}) facing {FacingLetter(facing)}, nothing to bar.");
            return;
        }
        var (bx, by, idx) = behind.Value;

        var occupied = BuildOccupancy();
        if (!BulwarkPolicy.IsVacant((bx, by), occupied))
        {
            _deferred[idx] = new DeferredTile(bx, by);
        }
        else
        {
            long addr = GridAddr(idx);
            if (_mem.Readable(addr, 1) && _mem.Writable(addr, 1))
            {
                byte orig = _mem.U8(addr);
                _mem.W8(addr, BulwarkPolicy.VetoedF6(orig));
                _restoreBook[idx] = orig;
            }
            else
            {
                // Vacant but unreadable/unwritable THIS tick: park it for the deferred watch,
                // which re-checks readability/writability on every raise attempt, so a transient
                // miss self-heals -- with a single tile, silently skipping would make the whole
                // stance a permanent no-op instead.
                _deferred[idx] = new DeferredTile(bx, by);
            }
        }

        _planted = true;
        ModLogger.Event(LogVerb.Signature, $"The Sunderer wielder plants its blade at ({gx},{gy}) facing {FacingLetter(facing)}; the ground at its back ({bx},{by}) is barred.");
        _toast.Enqueue(SundererId, Tuning.BulwarkToastKey, "The Sunderer plants its blade; none may take the ground at its back.");
    }

    /// <summary>Per-tick upkeep while planted: re-assert the restore-book tile if it drifted from
    /// its vetoed value (recomputed from the SAVED original, never re-sampled), then advance the
    /// deferred-tile vacancy watch (AC criterion 6: two consecutive vacant ticks before a raise,
    /// original captured AT RAISE TIME, not at deferral time).</summary>
    private void MaintainPlant()
    {
        foreach (var kv in _restoreBook)
        {
            long addr = GridAddr(kv.Key);
            byte vetoed = BulwarkPolicy.VetoedF6(kv.Value);
            if (_mem.Readable(addr, 1) && _mem.U8(addr) != vetoed && _mem.Writable(addr, 1))
                _mem.W8(addr, vetoed);
        }

        if (_deferred.Count == 0) return;
        var occupied = BuildOccupancy();
        foreach (int idx in new List<int>(_deferred.Keys))
        {
            var d = _deferred[idx];
            if (!BulwarkPolicy.IsVacant((d.X, d.Y), occupied)) { d.VacantStreak = 0; continue; }
            if (++d.VacantStreak < 2) continue;

            long addr = GridAddr(idx);
            if (!_mem.Readable(addr, 1) || !_mem.Writable(addr, 1)) continue;   // keep watching; unwritable this tick
            byte orig = _mem.U8(addr);   // captured HERE, at raise time -- never at deferral time
            _mem.W8(addr, BulwarkPolicy.VetoedF6(orig));
            _restoreBook[idx] = orig;
            _deferred.Remove(idx);
        }
    }

    /// <summary>RELEASE: write every restore-book original back (guarded AND ownership-verified --
    /// see below), clear the book and the deferred watch, log once -- UNLESS there was nothing to
    /// restore (both empty already), in which case it is a silent no-op. That silence is
    /// load-bearing: ResetBattle (Bulwark.cs's class doc, A1) now calls this on every battle edge,
    /// and Engine fires ResetBattle on BOTH the enter and exit edges, so a routine double-edge
    /// reset must not narrate a release that never happened.
    ///
    /// OWNERSHIP CHECK (LW-145 fix 5): every other restore surface in the repo verifies it still
    /// owns a byte before stamping a saved original back; this was the one that didn't. Restore
    /// ONLY when the tile still reads as OUR vetoed form of the saved original (BulwarkPolicy.
    /// VetoedF6(saved)) -- if another system rewrote it since we planted (a level script, another
    /// mod's terrain edit), stamping the stale saved original back would clobber a write we no
    /// longer own. A mismatch skips that byte and logs once; MaintainPlant already re-asserts the
    /// vetoed form from the saved original every tick the plant is held, so this check can never
    /// strand a healthy plant -- it only ever catches a genuine foreign write.</summary>
    private void Release(string reason)
    {
        bool hadState = _restoreBook.Count > 0 || _deferred.Count > 0;

        foreach (var kv in _restoreBook)
        {
            long addr = GridAddr(kv.Key);
            if (!_mem.Readable(addr, 1) || !_mem.Writable(addr, 1)) continue;
            if (_mem.U8(addr) != BulwarkPolicy.VetoedF6(kv.Value))
            {
                ModLogger.WarnWithTrace(LogVerb.Signature,
                    "Bulwark finds the ground behind the Sunderer was changed by something else since it planted, so it leaves that tile alone instead of overwriting the change.",
                    $"tile index {kv.Key}, addr 0x{addr:X}: expected vetoed byte 0x{BulwarkPolicy.VetoedF6(kv.Value):X2}, observed 0x{_mem.U8(addr):X2}.");
                continue;
            }
            _mem.W8(addr, kv.Value);
        }
        _restoreBook.Clear();
        _deferred.Clear();
        _planted = false;

        if (hadState)
            ModLogger.Event(LogVerb.Signature, $"The Sunderer's Bulwark releases ({reason}); the ground at its back settles back to normal.");
    }

    /// <summary>Every band seat that PLAUSIBLY holds a unit or corpse, keyed by (gx,gy): a LOOSE
    /// test (level byte 1..99, maxHp u16 1..9999 -- Band.cs's own offsets, but deliberately NOT
    /// Band.IsValid, which also checks brave/faith and is tighter than this needs). Over-blocking
    /// is the safe direction (AC criterion 5): a behind tile that reads occupied via a frozen-twin
    /// mirror or a stale seat only ever loses a block it could have taken, never blocks one it
    /// shouldn't.</summary>
    private HashSet<(int x, int y)> BuildOccupancy()
    {
        var occ = new HashSet<(int x, int y)>();
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            int lvl = _mem.U8(e + Offsets.ALevel);
            if (lvl < 1 || lvl > 99) continue;
            int mhp = _mem.U16(e + Offsets.AMaxHp);
            if (mhp < 1 || mhp > 9999) continue;
            occ.Add((_mem.U8(e + Offsets.AGx), _mem.U8(e + Offsets.AGy)));
        }
        return occ;
    }

    private static long GridAddr(int idx) => Offsets.PathTerrainGrid + (long)idx * Offsets.PathTerrainStride + Offsets.PathTerrainVetoField;
}
