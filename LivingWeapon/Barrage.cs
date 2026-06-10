using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Yoichi Bow's "Barrage" signature: while a +3 Yoichi Bow wielder exists in the roster,
/// ability 358 (Barrage, 4-arrow volley) is injected into their current job's JobCommand
/// record and held there each session. The table rebuilds at boot, so the injection MUST
/// be re-asserted each tick (idempotent: NeedsInject only writes if the slot is wrong).
///
/// RECORD SAVE: the 25-byte record (flag prefix + 16 ability bytes + 6 RSM bytes) is saved
/// ONCE on first inject. A re-save while injected would overwrite originals with injected
/// state, and the restore would restore zeros. Never-re-save is the invariant.
///
/// LEARNED BIT: set once in the roster's action bitfield (roster +0x32 + jobIdx*3, slot 10 =
/// byte1 0x40). NEVER cleared -- the bow teaches permanently; the residue is inert any session
/// the record isn't injected (battle menu needs the bit; the slot is blank-named).
///
/// GRANT END: when the wielder unequips / is gone / changes jobs mid-session, restore the
/// original record bytes and re-resolve the new state next tick.
///
/// NEEDS-LIVE-VERIFY (marked in CSV):
///   (1) Generic-job record mapping: job id -> JobCommand record index. Ramza (job 3) -> rec 27
///       proven; generic classes unverified. barrage_probe.py dump is the oracle.
///   (2) Learned-bit jobIdx: assumed == job id (roster +0x02). learned_probe.py is the oracle.
///
/// All reads/writes are VirtualQuery-guarded (Mem.Writable/Readable).
/// </summary>
internal sealed partial class Barrage
{
    // ABILITY_BASE: record 0's AbilityId1 byte. Flags sit at ABILITY_BASE + rec*25 - 3.
    // From barrage_probe.py: ABILITY_BASE = 0x140679436 - 27*25.
    private const long AbilityBase = 0x140679436L - 27L * 25;
    private const int RecSize = 25;         // bytes per record (3 flags + 16 abilities + 6 RSM)
    private const int FlagPrefixSize = 3;   // ExtAb u16 + ExtRSM u8
    private const int AbilityCount = 16;

    // Roster job id at +0x02 (UNIT_DATA_STRUCTURE.md, IC-verified).
    private const int RJobId = 0x02;
    // Roster learned bitfield at +0x32 + jobIdx*3; jobIdx assumed == job id (NEEDS-LIVE-VERIFY).
    private const int RLearnedBase = 0x32;
    private const int LearnedStride = 3;

    private const int YoichiId = 90;
    private const int BarrageAbilityId = 358;

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly BarrageState _state;
    private bool _wasActive;
    private int _lastRecId = -1;   // record id currently injected (-1 = none)

    public Barrage(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills)
    {
        _meta = meta;
        _kills = kills;
        _state = new BarrageState();
    }

    public void ResetBattle()
    {
        // Do NOT restore on battle exit; the record rebuilds at boot and the grant is
        // a session-long injection. Only restore on explicit grant-end or wielder change.
        _wasActive = false;
        // State (saved record + slot) persists across battles within one session.
    }

    public void Tick()
    {
        if (!_meta.TryGetValue(YoichiId, out var m) || m.Signature is null) return;
        int tier = Tuning.TierFor(_kills.TryGetValue(YoichiId, out int k) ? k : 0);
        bool active = IsActive(m.Signature, tier);

        // Find the wielder's roster slot (any slot holding the Yoichi Bow at +3).
        int wielderSlot = -1;
        int wielderJob = -1;
        if (active)
        {
            for (int r = 0; r < Offsets.RosterSlots; r++)
            {
                long rb = Offsets.RosterBase + (long)r * Offsets.RosterStride;
                if (!Mem.Readable(rb + Offsets.RNameId, 2)) continue;
                int lvl = Mem.U8(rb + Offsets.RLevel);
                if (lvl < 1 || lvl > 99) continue;
                if (Mem.U16(rb + Offsets.RRHand) != YoichiId && Mem.U16(rb + Offsets.ROffHand) != YoichiId) continue;
                int jobId = Mem.U8(rb + RJobId);
                if (jobId <= 0) continue;
                wielderSlot = r;
                wielderJob = jobId;
                break;
            }
        }

        bool nowActive = wielderSlot >= 0;
        if (nowActive != _wasActive)
        {
            _wasActive = nowActive;
            Log.Info($"barrage {(nowActive ? $"ACTIVE (Yoichi job {wielderJob} rec {wielderJob})" : "inactive")}");
        }

        if (!nowActive)
        {
            if (_lastRecId >= 0) { Restore(_lastRecId); _lastRecId = -1; }
            return;
        }

        int recId = wielderJob;   // NEEDS-LIVE-VERIFY: rec index == job id assumed (Ramza rec 27 proven)
        if (recId < 0 || recId >= 200) { Log.Info($"barrage: recId {recId} out of range, no-op"); return; }

        // Job changed mid-session: restore old record and re-inject for the new job.
        if (_lastRecId >= 0 && _lastRecId != recId)
        {
            Restore(_lastRecId);
            _state.Clear();
        }
        _lastRecId = recId;

        long flagAddr = AbilityBase + (long)recId * RecSize - FlagPrefixSize;
        long abBase = AbilityBase + (long)recId * RecSize;

        // Sane-bounds check before ANY write.
        if (!Mem.Readable(flagAddr, RecSize)) { Log.Info($"barrage: rec {recId} not readable, no-op"); return; }

        // Save the original record ONCE (never-re-save while injected).
        if (!_state.HasSaved(recId))
        {
            byte[] original = Mem.ReadBytes(flagAddr, RecSize);
            _state.Save(recId, original);
            Log.Info($"barrage: saved original rec {recId}");
        }

        // Find or verify the injection slot.
        int slotIdx = _state.SlotIdx;
        if (slotIdx < 0)
        {
            // Read current ability bytes to find first empty slot.
            if (!Mem.TryReadBytes(flagAddr, RecSize, out byte[] buf)) return;
            ushort extAb = (ushort)(buf[0] | (buf[1] << 8));
            var ab = new byte[AbilityCount];
            for (int i = 0; i < AbilityCount; i++) ab[i] = buf[FlagPrefixSize + i];
            int slot1 = FindEmptySlot(ab, extAb);
            if (slot1 < 0) { Log.Info($"barrage: no empty slot in rec {recId}"); return; }
            slotIdx = slot1 - 1;   // convert to 0-indexed
            _state.SlotIdx = slotIdx;
            Log.Info($"barrage: picked slot {slot1} (idx {slotIdx}) in rec {recId}");
        }

        // Idempotent inject: only write if the slot is not already correct.
        if (!Mem.TryReadBytes(flagAddr, RecSize, out byte[] cur)) return;
        ushort curExt = (ushort)(cur[0] | (cur[1] << 8));
        byte curByte = cur[FlagPrefixSize + slotIdx];
        if (NeedsInject(curByte, curExt, slotIdx))
            InjectSlot(flagAddr, abBase, slotIdx, BarrageAbilityId);

        // Set the learned bit (NEVER cleared). NEEDS-LIVE-VERIFY: jobIdx == job id assumed.
        SetLearnedBit(wielderSlot, wielderJob, slotIdx + 1);   // 1-indexed slot for learned math
    }

    /// <summary>Restore the original record bytes if we have them saved.</summary>
    private void Restore(int recId)
    {
        var saved = _state.GetSaved(recId);
        if (saved is null) return;
        long flagAddr = AbilityBase + (long)recId * RecSize - FlagPrefixSize;
        if (!Mem.Writable(flagAddr, RecSize)) return;
        RestoreRecord(flagAddr, saved);
        Log.Info($"barrage: restored rec {recId}");
    }

    /// <summary>Set the learned bit for the given 1-indexed action slot in the wielder's roster.
    /// Permanent write -- never cleared. NEEDS-LIVE-VERIFY: jobIdx == job id is assumed here.</summary>
    private static void SetLearnedBit(int rosterSlot, int jobId, int slotIdx1)
    {
        long rb = Offsets.RosterBase + (long)rosterSlot * Offsets.RosterStride;
        // jobIdx assumed == job id (NEEDS-LIVE-VERIFY).
        long learnBase = rb + RLearnedBase + (long)jobId * LearnedStride;
        int byteOff = LearnedByteIndex(slotIdx1);
        byte mask = LearnedBitMask(slotIdx1);
        long addr = learnBase + byteOff;
        if (!Mem.Readable(addr, 1)) return;
        byte cur = Mem.U8(addr);
        if ((cur & mask) != 0) return;   // already set -> no write needed
        if (!Mem.Writable(addr, 1)) return;
        Mem.W8(addr, (byte)(cur | mask));
        Log.Info($"barrage: learned bit set for rosterSlot {rosterSlot} job {jobId} slot {slotIdx1}");
    }
}
