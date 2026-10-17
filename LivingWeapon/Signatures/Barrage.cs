using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Yoichi Bow signature. While a +3 Yoichi Bow wielder is in the roster, ability 358 (Barrage, 4-arrow volley) is injected into their job's JobCommand
/// record and re-asserted every tick, in and out of battle (the table rebuilds at boot and the learn screen and pre-battle menus read it live).
/// Only the empty slot we filled is touched (Barrage.Policy.ReleaseSlot), so ShadowBlade can share the record and abilities others add survive.
/// BarrageState.RecId/SlotIdx (Barrage.State.cs) move as a pair and clear on every path that ends a grant.
/// The learned bit is held set, never cleared. The learn menu's purchase writeback wiped it (seen 2026-06-10, High Ether purchase).
/// Jobs (Barrage.Policy.cs): roster byte +0x02, band 74..92 -> rec = job-69, learned jobIdx = job-74 (Dancer 92 shares jobIdx 17). Unmapped jobs
/// (Mettle/Aimed Shot uniques, Mime, monsters, Dark Knight) log once per job id and no-op; noted in the bows CSV.
/// </summary>
internal sealed partial class Barrage : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick();
    // ABILITY_BASE. Record 0's AbilityId1 byte; flags sit at ABILITY_BASE + rec*25, 3.
    // 1.5 re-found 2026-06-17 (tools/probes/jobcommand_find_probe.py, read-only): the recompile moved the JobCommand table +0x5080.
    // Found by the rec 8 Aim (bytes 150-157) + rec 9 Martial Arts (bytes 100-107) signature, 25 bytes apart, a unique hit; the rest of
    // the table read coherently (Steal rec 14 = 108-115, Iaido rec 19, Machinist rec 37 = 213-215). The delta is not monotonic vs the
    // +0x6000 of the 0x14077 region, so it was found, not interpolated (was 0x140679436, 27*25 = 0x140679193 pre-1.5).
    // Pinned by BarrageTests.AbilityBase_is_pinned_to_the_verified_1_5_table_base. Internal because ShadowBlade reuses these
    // constants and helpers (shared inject/restore core is a future extraction, see ShadowBlade.cs).
    internal static long AbilityBase => ImageBase.At(0x14067E213L);
    internal const int RecSize = 25;         // bytes per record (3 flags + 16 abilities + 6 RSM)
    internal const int FlagPrefixSize = 3;   // ExtAb u16 + ExtRSM u8
    internal const int AbilityCount = 16;

    // Roster job id at +0x02 (FFTHandsFree UNIT_DATA_STRUCTURE.md; live-read 77 = Archer).
    internal const int RJobId = 0x02;
    // Roster secondary command at +0x07 = the JobCommand rec id (live-read 14 = Steal).
    internal const int RSecondary = 0x07;
    // Roster learned bitfield at +0x32 + jobIdx*3 (jobIdx from TryResolveJob, not the job id).
    internal const int RLearnedBase = 0x32;
    internal const int LearnedStride = 3;

    private const int YoichiId = 90;
    private const int BarrageAbilityId = 358;

    private readonly IGameMemory _mem;    // injected (LiveMemory in production; fakes in tests)

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly BarrageState _state; // atomic (RecId, SlotIdx) pair; RecId doubles as "record currently targeted"
    private bool _wasActive;
    private int _lastUnsupportedJob = -1; // log-once guard for unmapped jobs
    private bool _recNotReadableLogged;    // Signatures.StuckEdge latch. Record unreadable nag
    private bool _noSlotLogged;            // Signatures.StuckEdge latch. No empty slot nag

    public Barrage(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, IGameMemory? mem = null)
    {
        _mem = mem ?? new LiveMemory();
        _meta = meta;
        _kills = kills;
        _state = new BarrageState();
    }

    public void ResetBattle()
    {
        // No release on battle exit. The grant lasts the session while the bow stays equipped.
        _wasActive = false;
        _recNotReadableLogged = false;
        _noSlotLogged = false;
    }

    public void Tick()
    {
        if (!_meta.TryGetValue(YoichiId, out var m) || m.Signature is null) return;
        int tier = Tuning.TierOf(_kills, YoichiId);
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
                if (!_mem.Readable(rb + Offsets.RNameId, 2)) continue;
                int lvl = _mem.U8(rb + Offsets.RLevel);
                if (lvl < 1 || lvl > 99) continue;
                // Signatures fire from the main hand only. An offhand Yoichi does not grant Barrage.
                if (_mem.U16(rb + Offsets.RRHand) != YoichiId) continue;
                int jobId = _mem.U8(rb + RJobId);
                if (jobId <= 0) continue;
                int secRec = _mem.U8(rb + RSecondary);
                if (!IsEligibleWielder(jobId, secRec)) continue;   // Thief-only. Primary job 83 or secondary record 14
                wielderSlot = r;
                wielderJob = jobId;
                wielderSecondary = secRec;
                break;
            }
        }

        bool nowActive = wielderSlot >= 0;
        if (!nowActive)
        {
            if (_wasActive) ModLogger.Event(LogVerb.Grant, "Barrage is no longer granted; no eligible Yoichi Bow wielder remains");
            _wasActive = false;
            Restore();   // no-ops cleanly when nothing is injected; always clears bookkeeping
            return;
        }

        // Resolve the grant target. Primary job's record, else the mounted secondary's record
        // (special-executor primaries like Aim silently drop foreign abilities at confirm).
        if (!TryResolveGrant(wielderJob, wielderSecondary, out int recId, out int jobIdx, out bool viaSecondary))
        {
            if (_lastUnsupportedJob != wielderJob)
            {
                _lastUnsupportedJob = wielderJob;
                ModLogger.WarnWithTrace(LogVerb.Grant,
                    $"{LogNames.Job(wielderJob)} cannot receive Barrage; this job's action menu silently drops added abilities, or it is a unique story job",
                    $"barrage ungrantable (job {wielderJob}, secondary command {wielderSecondary})");
            }
            Restore();
            _wasActive = false;
            return;
        }

        if (!_wasActive)
        {
            _wasActive = true;
            string thiefPath = wielderJob == ThiefJob
                ? "Thief is the primary job"
                : "Thief (Steal) is the secondary command";
            ModLogger.EventWithTrace(LogVerb.Grant,
                $"The Yoichi Bow wielder ({thiefPath}) now has Barrage in {LogNames.Job(wielderJob)}'s action list{(viaSecondary ? " (via their secondary command)" : "")}",
                $"barrage grant (party slot {wielderSlot}, record {recId}, learn index {jobIdx})");
        }

        // Job changed mid-session. Release the old record's slot first; Restore clears BarrageState.RecId so the
        // lookup below re-runs FindEmptySlot on the new record.
        if (_state.RecId >= 0 && _state.RecId != recId) Restore();

        long flagAddr = AbilityBase + (long)recId * RecSize - FlagPrefixSize;
        long abBase = AbilityBase + (long)recId * RecSize;

        // Bounds check before any write. Debug tier with a once-per-transition latch. This can hold for many 33ms ticks while the table loads.
        bool recNotReadable = !_mem.Readable(flagAddr, RecSize);
        if (Signatures.StuckEdge(ref _recNotReadableLogged, recNotReadable))
            ModLogger.Debug(LogVerb.Grant, $"command record {recId} is not readable in memory yet; skipping this tick");
        if (recNotReadable) return;

        // Only trust a remembered slot found in this record (the cross-record staleness Restore handles).
        int slotIdx = _state.RecId == recId ? _state.SlotIdx : -1;
        if (slotIdx < 0)
        {
            if (!_mem.TryReadBytes(flagAddr, RecSize, out byte[] buf)) return;
            ushort extAb = (ushort)(buf[0] | (buf[1] << 8));
            var ab = new byte[AbilityCount];
            for (int i = 0; i < AbilityCount; i++) ab[i] = buf[FlagPrefixSize + i];
            int slot1 = FindEmptySlot(ab, extAb);
            if (slot1 < 0)
            {
                // Debug tier + latch. The condition can hold every tick until a slot frees up.
                if (Signatures.StuckEdge(ref _noSlotLogged, true))
                    ModLogger.Debug(LogVerb.Grant, $"no empty ability slot in the {LogNames.Job(wielderJob)} command list (record {recId}); cannot inject Barrage");
                return;
            }
            Signatures.StuckEdge(ref _noSlotLogged, false);   // slot found -> re-arm for next time
            slotIdx = slot1 - 1;   // convert to 0-indexed
            _state.Set(recId, slotIdx);
            ModLogger.Debug(LogVerb.Grant, $"placed Barrage in ability slot {slot1} of {LogNames.Job(wielderJob)}'s action list (slot index {slotIdx}, record {recId})");
        }

        // Idempotent inject. Only write when the slot is not already correct.
        if (!_mem.TryReadBytes(flagAddr, RecSize, out byte[] cur)) return;
        ushort curExt = (ushort)(cur[0] | (cur[1] << 8));
        byte curByte = cur[FlagPrefixSize + slotIdx];
        if (NeedsInject(curByte, curExt, slotIdx))
            InjectSlot(_mem, flagAddr, abBase, slotIdx, BarrageAbilityId);

        // Hold the learned bit (re-set whenever clear, never cleared).
        HoldLearnedBit(wielderSlot, jobIdx, slotIdx + 1);   // 1-indexed slot for learned math
    }

    /// <summary>Releases our injected slot (if any) and forgets it, even if the release is refused (see BarrageState.Clear).
    /// Called when the grant ends or the wielder's job changes record.</summary>
    private void Restore()
    {
        int recId = _state.RecId;
        int slotIdx = _state.SlotIdx;
        if (recId >= 0 && slotIdx >= 0)
        {
            long flagAddr = AbilityBase + (long)recId * RecSize - FlagPrefixSize;
            long abBase = AbilityBase + (long)recId * RecSize;
            bool released = ReleaseSlot(_mem, flagAddr, abBase, slotIdx, BarrageAbilityId);
            if (released)
                ModLogger.EventWithTrace(LogVerb.Grant,
                    "Barrage was removed from the job's action list; the rest of the list is untouched",
                    $"barrage release (record {recId}, slot {slotIdx})");
            else
                ModLogger.WarnWithTrace(LogVerb.Grant,
                    "Could not remove Barrage from its slot; something else changed it first",
                    $"barrage release refused (record {recId}, slot {slotIdx})");
        }
        _state.Clear();
    }

    /// <summary>Holds the learned bit for the 1-indexed action slot in the wielder's roster (jobIdx triple), re-setting it whenever clear.</summary>
    private void HoldLearnedBit(int rosterSlot, int jobIdx, int slotIdx1)
    {
        long rb = Offsets.RosterBase + (long)rosterSlot * Offsets.RosterStride;
        long addr = rb + RLearnedBase + (long)jobIdx * LearnedStride + LearnedByteIndex(slotIdx1);
        byte mask = LearnedBitMask(slotIdx1);
        if (!_mem.Readable(addr, 1)) return;
        byte cur = _mem.U8(addr);
        if ((cur & mask) != 0) return;   // already set -> no write needed
        if (!_mem.Writable(addr, 1)) return;
        _mem.W8(addr, (byte)(cur | mask));
        ModLogger.Debug(LogVerb.Grant, $"re-set the learned flag for Barrage in party slot {rosterSlot} (job index {jobIdx}, ability slot {slotIdx1}); menu write-back cleared it");
    }
}
