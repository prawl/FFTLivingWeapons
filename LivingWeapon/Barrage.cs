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
internal sealed partial class Barrage : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick();
    // ABILITY_BASE: record 0's AbilityId1 byte. Flags sit at ABILITY_BASE + rec*25 - 3.
    // 1.5 RE-FOUND LIVE 2026-06-17 (tools/probes/jobcommand_find_probe.py, READ-ONLY): the
    // recompile moved the JobCommand table +0x5080 (the most dangerous WRITE anchor in the port).
    // Located by the rec 8 Aim (bytes 150-157) + rec 9 Martial Arts (bytes 100-107) signature,
    // 25 bytes apart -- UNIQUE hit; the whole table then read coherently (Steal rec 14 = 108-115,
    // Iaido rec 19, Machinist rec 37 = 213-215), so "stale-but-valid" is ruled out. Delta is
    // NON-MONOTONIC vs the +0x6000 of the 0x14077 region -- found by signature, NOT interpolated.
    // Pinned by BarrageTests.AbilityBase_is_pinned_to_the_verified_1_5_table_base.
    // (was 0x140679436 - 27*25 = 0x140679193 pre-1.5.)
    // internal (not private): ShadowBlade reuses these table-layout constants + the static helpers
    // below to grant Shadow Blade through the same proven JobCommand injection -- the Barrage logic
    // itself is untouched. (The shared inject/restore core is a future extraction, see ShadowBlade.cs.)
    internal const long AbilityBase = 0x14067E213L;
    internal const int RecSize = 25;         // bytes per record (3 flags + 16 abilities + 6 RSM)
    internal const int FlagPrefixSize = 3;   // ExtAb u16 + ExtRSM u8
    internal const int AbilityCount = 16;

    // Roster job id at +0x02 (FFTHandsFree UNIT_DATA_STRUCTURE.md; live-read 77 = Archer).
    internal const int RJobId = 0x02;
    // Roster secondary command at +0x07 = the JobCommand rec id (live-read 14 = Steal).
    internal const int RSecondary = 0x07;
    // Roster learned bitfield at +0x32 + jobIdx*3 (jobIdx from TryResolveJob, NOT the job id).
    internal const int RLearnedBase = 0x32;
    internal const int LearnedStride = 3;

    private const int YoichiId = 90;
    private const int BarrageAbilityId = 358;

    private readonly IGameMemory _mem;    // injected (LiveMemory in production; fakes in tests)

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly BarrageState _state;
    private bool _wasActive;
    private int _lastRecId = -1;          // record id currently injected (-1 = none)
    private int _lastUnsupportedJob = -1; // log-once guard for unmapped jobs

    public Barrage(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, IGameMemory? mem = null)
    {
        _mem = mem ?? new LiveMemory();
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
                // Signatures fire from the main hand only: an offhand Yoichi does not grant Barrage.
                if (_mem.U16(rb + Offsets.RRHand) != YoichiId) continue;
                int jobId = _mem.U8(rb + RJobId);
                if (jobId <= 0) continue;
                int secRec = _mem.U8(rb + RSecondary);
                if (!IsEligibleWielder(jobId, secRec)) continue;   // THIEF-ONLY: primary job 83 or secondary record 14
                wielderSlot = r;
                wielderJob = jobId;
                wielderSecondary = secRec;
                break;
            }
        }

        bool nowActive = wielderSlot >= 0;
        if (!nowActive)
        {
            if (_wasActive) Log.Info("barrage: Barrage signature no longer active -- no Yoichi Bow wielder with required kills");
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
                Log.Info($"barrage: {LogNames.Job(wielderJob)} (job {wielderJob}) cannot receive Barrage -- job uses a special command executor or is story-unique; no grant (secondary record {wielderSecondary})");
            }
            if (_lastRecId >= 0) { Restore(_lastRecId); _lastRecId = -1; }
            _wasActive = false;
            return;
        }

        if (!_wasActive)
        {
            _wasActive = true;
            string thiefPath = wielderJob == ThiefJob
                ? "Thief is the primary job"
                : "Thief (Steal) is the secondary command";
            Log.Info($"barrage: ACTIVE -- party slot {wielderSlot} wields Yoichi Bow ({thiefPath}), Barrage added to record {recId} (job {wielderJob}, learn-index {jobIdx}{(viaSecondary ? ", injected via secondary" : "")})");
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
        if (!_mem.Readable(flagAddr, RecSize)) { Log.Info($"barrage: command record {recId} is not readable in memory yet -- skipping this tick"); return; }

        // Save the original record ONCE (never-re-save while injected).
        if (!_state.HasSaved(recId))
        {
            byte[] original = _mem.ReadBytes(flagAddr, RecSize);
            _state.Save(recId, original);
            Log.Info($"barrage: saved original {LogNames.Job(wielderJob)} command list before injecting Barrage (record {recId})");
        }

        // Find or verify the injection slot.
        int slotIdx = _state.SlotIdx;
        if (slotIdx < 0)
        {
            if (!_mem.TryReadBytes(flagAddr, RecSize, out byte[] buf)) return;
            ushort extAb = (ushort)(buf[0] | (buf[1] << 8));
            var ab = new byte[AbilityCount];
            for (int i = 0; i < AbilityCount; i++) ab[i] = buf[FlagPrefixSize + i];
            int slot1 = FindEmptySlot(ab, extAb);
            if (slot1 < 0) { Log.Info($"barrage: no empty ability slot in the {LogNames.Job(wielderJob)} command list (record {recId}) -- cannot inject Barrage"); return; }
            slotIdx = slot1 - 1;   // convert to 0-indexed
            _state.SlotIdx = slotIdx;
            Log.Info($"barrage: picked ability slot {slot1} in the {LogNames.Job(wielderJob)} command list (slot index {slotIdx}, record {recId})");
        }

        // Idempotent inject: only write when the slot is not already correct.
        if (!_mem.TryReadBytes(flagAddr, RecSize, out byte[] cur)) return;
        ushort curExt = (ushort)(cur[0] | (cur[1] << 8));
        byte curByte = cur[FlagPrefixSize + slotIdx];
        if (NeedsInject(curByte, curExt, slotIdx))
            InjectSlot(_mem, flagAddr, abBase, slotIdx, BarrageAbilityId);

        // Hold the learned bit (re-set whenever clear; NEVER cleared by us).
        HoldLearnedBit(wielderSlot, jobIdx, slotIdx + 1);   // 1-indexed slot for learned math
    }

}
