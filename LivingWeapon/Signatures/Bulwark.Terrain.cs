using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Bulwark's terrain half. Plant writes the obstacle bit behind the wielder (or parks the tile if
/// it is occupied), MaintainPlant re-asserts it each tick, Release restores the saved bytes.
/// Bulwark.cs holds the trigger logic and the mechanism notes.
/// </summary>
internal sealed partial class Bulwark
{
    /// <summary>A behind tile that is occupied, or vacant but unreadable at plant time. It is raised
    /// after two consecutive vacant ticks (AC criterion 6), which also retries a transient memory miss.
    /// Mutable; the per-tick watch updates it in place.</summary>
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

    /// <summary>Reads the wielder's tile, layer and facing from one byte (Offsets.ALayerBit), checks the
    /// map dims (AC A3), and bars the tile behind the wielder, or parks it if occupied or not yet writable.
    /// Plants nothing, and toasts nothing, when the wielder's back is to the map edge.</summary>
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
                // Unreadable or unwritable this tick. Park it so the deferred watch retries; skipping it would
                // leave the stance a permanent no-op.
                _deferred[idx] = new DeferredTile(bx, by);
            }
        }

        _planted = true;
        ModLogger.Event(LogVerb.Signature, $"The Sunderer wielder plants its blade at ({gx},{gy}) facing {FacingLetter(facing)}; the ground at its back ({bx},{by}) is barred.");
        _toast.Enqueue(SundererId, Tuning.BulwarkToastKey, "The Sunderer plants its blade; none may take the ground at its back.");
    }

    /// <summary>Re-asserts each restore-book tile from its saved original, then advances the deferred
    /// vacancy watch (AC criterion 6). The original is captured at raise time.</summary>
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

    /// <summary>Writes every saved original back, clears the book and the deferred watch, and logs once.
    /// Silent when there was nothing to restore, because ResetBattle calls this on both battle edges
    /// (Bulwark.cs, A1).
    ///
    /// LW-145 fix 5: a byte is restored only if it still reads as our vetoed form of the saved original.
    /// If something else (a level script, another mod) rewrote it, restoring would clobber that write,
    /// so the tile is skipped and logged. MaintainPlant keeps a healthy plant at the vetoed form, so
    /// this only catches foreign writes.</summary>
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

    /// <summary>Every band seat that plausibly holds a unit or corpse, keyed by (gx,gy), using a loose
    /// level/maxHp range test rather than Band.IsValid. Over-blocking is the safe direction (AC
    /// criterion 5): a stale seat can only cost a block, never allow a bad one.</summary>
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
