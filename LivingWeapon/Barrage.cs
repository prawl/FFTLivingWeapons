using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Yoichi Bow's "Barrage" signature: while a +3 Yoichi Bow wielder exists in the roster,
/// ability 358 (Barrage, 4-arrow volley) is injected into their current job's JobCommand
/// record and held there. The table rebuilds at boot, so the injection is re-asserted each
/// tick (idempotent: NeedsInject only writes when the slot is wrong). Ticked in AND out of
/// battle -- the learn screen and pre-battle menus read the table live.
///
/// RECORD SAVE: the 25-byte record (flag prefix + 16 ability bytes + 6 RSM bytes) is saved
/// ONCE on first inject. A re-save while injected would capture injected state and make the
/// restore restore the injection. Never-re-save is the invariant.
///
/// LEARNED BIT: HELD each tick (re-set whenever clear) and NEVER cleared. Set-once is not
/// enough: the learn menu's purchase flow writes the learned block back from a stale
/// snapshot and wipes externally-set bits (proven live 2026-06-10, High Ether purchase).
/// The wipe risk only matters while the menu is open; the 33ms hold closes it.
///
/// JOB RESOLUTION (live-anchored, see Barrage.Policy.cs): roster job byte +0x02; generic
/// band 74..92 -> rec = job-69, learned jobIdx = job-74 (Dancer 92 shares jobIdx 17).
/// Unmapped (story-canonical uniques like Mettle/Aimed Shot, Mime, monsters, Dark Knight):
/// log once per job id and no-op -- noted in the bows CSV as a known gap.
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

    // Roster job id at +0x02 (FFTHandsFree UNIT_DATA_STRUCTURE.md; live-read 77 = Archer).
    private const int RJobId = 0x02;
    // Roster secondary command at +0x07 = the JobCommand rec id (live-read 14 = Steal).
    private const int RSecondary = 0x07;
    // Roster learned bitfield at +0x32 + jobIdx*3 (jobIdx from TryResolveJob, NOT the job id).
    private const int RLearnedBase = 0x32;
    private const int LearnedStride = 3;

    private const int YoichiId = 90;
    private const int BarrageAbilityId = 358;

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly BarrageState _state;
    private bool _wasActive;
    private int _lastRecId = -1;          // record id currently injected (-1 = none)
    private int _lastUnsupportedJob = -1; // log-once guard for unmapped jobs

    public Barrage(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills)
    {
        _meta = meta;
        _kills = kills;
        _state = new BarrageState();
    }

    public void ResetBattle()
    {
        // No restore on battle exit: the grant is session-long while the bow stays equipped.
        // Saved-record state persists across battles within one session.
        _wasActive = false;
    }

    public void Tick()
    {
        if (!_meta.TryGetValue(YoichiId, out var m) || m.Signature is null) return;
        int tier = Tuning.TierFor(_kills.TryGetValue(YoichiId, out int k) ? k : 0);
        bool active = IsActive(m.Signature, tier);

        // Find the wielder's roster slot (any slot holding the Yoichi Bow).
        int wielderSlot = -1;
        int wielderJob = -1;
        int wielderSecondary = -1;
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
                if (!IsEligibleWielder(jobId)) continue;   // THIEF-ONLY (card states it; engine walls every other job)
                wielderSlot = r;
                wielderJob = jobId;
                wielderSecondary = Mem.U8(rb + RSecondary);
                break;
            }
        }

        bool nowActive = wielderSlot >= 0;
        if (!nowActive)
        {
            if (_wasActive) Log.Info("barrage inactive");
            _wasActive = false;
            if (_lastRecId >= 0) { Restore(_lastRecId); _lastRecId = -1; }
            return;
        }

        // Resolve the grant target: primary job's record, else the mounted secondary's record
        // (special-executor primaries like Aim silently drop foreign abilities at confirm).
        if (!TryResolveGrant(wielderJob, wielderSecondary, out int recId, out int jobIdx, out bool viaSecondary))
        {
            if (_lastUnsupportedJob != wielderJob)
            {
                _lastUnsupportedJob = wielderJob;
                Log.Info($"barrage: job {wielderJob} sec {wielderSecondary} ungrantable (special-executor/story-unique/monster) -> no grant");
            }
            if (_lastRecId >= 0) { Restore(_lastRecId); _lastRecId = -1; }
            _wasActive = false;
            return;
        }

        if (!_wasActive)
        {
            _wasActive = true;
            Log.Info($"barrage ACTIVE (Yoichi slot {wielderSlot} job {wielderJob} -> rec {recId} jobIdx {jobIdx}{(viaSecondary ? " via secondary" : "")})");
        }

        // Job changed mid-session: restore the old record and re-inject for the new job.
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

        // Idempotent inject: only write when the slot is not already correct.
        if (!Mem.TryReadBytes(flagAddr, RecSize, out byte[] cur)) return;
        ushort curExt = (ushort)(cur[0] | (cur[1] << 8));
        byte curByte = cur[FlagPrefixSize + slotIdx];
        if (NeedsInject(curByte, curExt, slotIdx))
            InjectSlot(flagAddr, abBase, slotIdx, BarrageAbilityId);

        // Hold the learned bit (re-set whenever clear; NEVER cleared by us).
        HoldLearnedBit(wielderSlot, jobIdx, slotIdx + 1);   // 1-indexed slot for learned math
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

    /// <summary>Hold the learned bit for the given 1-indexed action slot in the wielder's roster
    /// (jobIdx triple). Re-sets whenever clear -- the learn menu's purchase writeback can wipe
    /// externally-set bits. Never cleared by us.</summary>
    private static void HoldLearnedBit(int rosterSlot, int jobIdx, int slotIdx1)
    {
        long rb = Offsets.RosterBase + (long)rosterSlot * Offsets.RosterStride;
        long addr = rb + RLearnedBase + (long)jobIdx * LearnedStride + LearnedByteIndex(slotIdx1);
        byte mask = LearnedBitMask(slotIdx1);
        if (!Mem.Readable(addr, 1)) return;
        byte cur = Mem.U8(addr);
        if ((cur & mask) != 0) return;   // already set -> no write needed
        if (!Mem.Writable(addr, 1)) return;
        Mem.W8(addr, (byte)(cur | mask));
        Log.Info($"barrage: learned bit held (rosterSlot {rosterSlot} jobIdx {jobIdx} slot {slotIdx1})");
    }
}
