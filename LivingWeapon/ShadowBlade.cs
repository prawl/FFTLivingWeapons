using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Sanguine Sword's "Shadow Blade" signature: grant the wielder Gaffgarion's Shadow Blade -- a ranged
/// dark strike that absorbs the foe's HP -- via JobCommand injection. It REUSES every proven Barrage
/// primitive (the table-layout consts + TryResolveGrant / FindEmptySlot / ExtendBit / SlotByte /
/// InjectSlot / RestoreRecord / learned-bit math + BarrageState); the shipped Barrage module is not
/// touched. Only three things differ from Barrage and live here:
///   - the granted ability is read from meta (GrantCommandAbilityId), not hardcoded to 358;
///   - eligibility is a WHITELIST of sword skill-sets -- Squire/Knight (records 5/7) -- reached via
///     the wielder's primary job OR a mounted secondary command (ShadowBladePolicy.TryResolveGrant),
///     where Barrage is Thief-only; the resolution is otherwise identical;
///   - the idempotent-inject check is general for any id (ShadowBladePolicy.NeedsInject).
///
/// FOLLOW-UP SEAM: the save/inject/restore/hold orchestration below is structurally Barrage's. Once
/// Shadow Blade is live-verified, the shared core should be extracted into one helper both call --
/// deferred deliberately, so a blind refactor can't regress the SHIPPED Barrage path.
///
/// LIVE-PENDING (only a deploy can settle, the same class Barrage needed): does Shadow Blade render
/// with its real menu NAME (the blank-name risk -- it has a table name, so it SHOULD), and does
/// every resolvable job actually EXECUTE it (the special-executor swallow)? All reads/writes are
/// VirtualQuery-guarded; the record is saved-once and restored, so a wrong guess reverts cleanly.
///
/// RESTORE BLAST RADIUS (inherited from Barrage's save-once/full-record-restore, wider here because
/// the grant can target either whitelisted record 5/7, not just Thief's rec 14): if the player legitimately
/// learns a NEW ability into the SAME record AFTER our first save, a later Restore reverts that
/// record to the pre-inject snapshot, dropping the learned ability. Same latent behavior as Barrage;
/// a precise fix (restore only the injected slot) belongs in the future shared-core extraction.
/// </summary>
internal sealed class ShadowBlade : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick();
    private const int SanguineId = 23;

    private readonly IGameMemory _mem;
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly BarrageState _state = new();   // same save/slot ledger Barrage uses
    private bool _wasActive;
    private int _lastRecId = -1;
    private int _lastUnsupportedJob = -1;
    private bool _noSlotLogged;   // Signatures.StuckEdge latch: no empty slot nag

    public ShadowBlade(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, IGameMemory? mem = null)
    {
        _mem = mem ?? new LiveMemory();
        _meta = meta;
        _kills = kills;
    }

    public void ResetBattle()
    {
        _wasActive = false;   // session-long grant; saved record persists across battles
        _noSlotLogged = false;
    }

    public void Tick()
    {
        if (!_meta.TryGetValue(SanguineId, out var m) || m.Signature is null) return;
        int tier = Tuning.TierOf(_kills, SanguineId);
        int abilityId = m.Signature.GrantCommandAbilityId;
        bool active = ShadowBladePolicy.IsActive(m.Signature, tier);

        // Find the eligible Sanguine Sword wielder (main-hand only, any normal-executor job).
        int wielderSlot = -1, wielderJob = -1, wielderSecondary = -1;
        if (active)
        {
            for (int r = 0; r < Offsets.RosterSlots; r++)
            {
                long rb = Offsets.RosterBase + (long)r * Offsets.RosterStride;
                if (!_mem.Readable(rb + Offsets.RNameId, 2)) continue;
                int lvl = _mem.U8(rb + Offsets.RLevel);
                if (lvl < 1 || lvl > 99) continue;
                if (_mem.U16(rb + Offsets.RRHand) != SanguineId) continue;   // signatures fire from the main hand
                int jobId = _mem.U8(rb + Barrage.RJobId);
                if (jobId <= 0) continue;
                int secRec = _mem.U8(rb + Barrage.RSecondary);
                if (!ShadowBladePolicy.IsEligible(jobId, secRec)) continue;
                wielderSlot = r; wielderJob = jobId; wielderSecondary = secRec;
                break;
            }
        }

        if (wielderSlot < 0)
        {
            if (_wasActive) ModLogger.Log("shadow blade: signature no longer active -- no eligible Sanguine Sword wielder");
            _wasActive = false;
            // Clear the ledger too (not just _lastRecId): eligibility is OPEN, so the NEXT grant can
            // resolve to a DIFFERENT record. A stale SlotIdx would inject there without re-finding an
            // empty slot, overwriting a real ability. (Barrage is Thief-only/constant-record, so it
            // never hits this -- the open eligibility is what makes it reachable.)
            if (_lastRecId >= 0) { Restore(_lastRecId); _state.Clear(); _lastRecId = -1; }
            return;
        }

        if (!ShadowBladePolicy.TryResolveGrant(wielderJob, wielderSecondary, out int recId, out int jobIdx, out bool viaSecondary))
        {
            if (_lastUnsupportedJob != wielderJob)
            {
                _lastUnsupportedJob = wielderJob;
                ModLogger.Log($"shadow blade: {LogNames.Job(wielderJob)} (job {wielderJob}) cannot receive Shadow Blade -- needs Squire or Knight as the primary job, or one of those job's action sets equipped as the secondary command [secondary command {wielderSecondary}]");
            }
            if (_lastRecId >= 0) { Restore(_lastRecId); _lastRecId = -1; }
            _wasActive = false;
            return;
        }

        if (!_wasActive)
        {
            _wasActive = true;
            ModLogger.Log($"shadow blade: ACTIVE -- party slot {wielderSlot} wields Sanguine Sword, Shadow Blade is now in {LogNames.Job(wielderJob)}'s action list{(viaSecondary ? " (via their secondary command)" : "")} [ability {abilityId}, record {recId}, learn-index {jobIdx}]");
        }

        // Job changed mid-session: restore the old record, re-inject for the new one.
        if (_lastRecId >= 0 && _lastRecId != recId) { Restore(_lastRecId); _state.Clear(); }
        _lastRecId = recId;

        long flagAddr = Barrage.AbilityBase + (long)recId * Barrage.RecSize - Barrage.FlagPrefixSize;
        long abBase = Barrage.AbilityBase + (long)recId * Barrage.RecSize;
        if (!_mem.Readable(flagAddr, Barrage.RecSize)) { return; }

        if (!_state.HasSaved(recId))
        {
            _state.Save(recId, _mem.ReadBytes(flagAddr, Barrage.RecSize));
            ModLogger.Log($"shadow blade: backed up {LogNames.Job(wielderJob)}'s original action list before adding Shadow Blade [record {recId}]");
        }

        int slotIdx = _state.SlotIdx;
        if (slotIdx < 0)
        {
            if (!_mem.TryReadBytes(flagAddr, Barrage.RecSize, out byte[] buf)) return;
            ushort extAb = (ushort)(buf[0] | (buf[1] << 8));
            var ab = new byte[Barrage.AbilityCount];
            for (int i = 0; i < Barrage.AbilityCount; i++) ab[i] = buf[Barrage.FlagPrefixSize + i];
            int slot1 = Barrage.FindEmptySlot(ab, extAb);
            if (slot1 < 0)
            {
                // Debug tier + latch: the condition can hold every tick until a slot frees up.
                if (Signatures.StuckEdge(ref _noSlotLogged, true))
                    ModLogger.LogDebug($"shadow blade: no empty ability slot in the {LogNames.Job(wielderJob)} command list (record {recId}) -- cannot inject Shadow Blade");
                return;
            }
            Signatures.StuckEdge(ref _noSlotLogged, false);   // slot found -> re-arm for next time
            slotIdx = slot1 - 1;
            _state.SlotIdx = slotIdx;
            ModLogger.Log($"shadow blade: placed Shadow Blade in ability slot {slot1} of {LogNames.Job(wielderJob)}'s action list [record {recId}]");
        }

        if (!_mem.TryReadBytes(flagAddr, Barrage.RecSize, out byte[] cur)) return;
        ushort curExt = (ushort)(cur[0] | (cur[1] << 8));
        byte curByte = cur[Barrage.FlagPrefixSize + slotIdx];
        if (ShadowBladePolicy.NeedsInject(curByte, curExt, slotIdx, abilityId))
            Barrage.InjectSlot(_mem, flagAddr, abBase, slotIdx, abilityId);

        HoldLearnedBit(wielderSlot, jobIdx, slotIdx + 1);
    }

    private void Restore(int recId)
    {
        var saved = _state.GetSaved(recId);
        if (saved is null) return;
        long flagAddr = Barrage.AbilityBase + (long)recId * Barrage.RecSize - Barrage.FlagPrefixSize;
        if (!_mem.Writable(flagAddr, Barrage.RecSize)) return;
        Barrage.RestoreRecord(_mem, flagAddr, saved);
        ModLogger.Log($"shadow blade: removed Shadow Blade from the job's action list, restored to vanilla [record {recId}]");
    }

    private void HoldLearnedBit(int rosterSlot, int jobIdx, int slotIdx1)
    {
        long rb = Offsets.RosterBase + (long)rosterSlot * Offsets.RosterStride;
        long addr = rb + Barrage.RLearnedBase + (long)jobIdx * Barrage.LearnedStride + Barrage.LearnedByteIndex(slotIdx1);
        byte mask = Barrage.LearnedBitMask(slotIdx1);
        if (!_mem.Readable(addr, 1)) return;
        byte cur = _mem.U8(addr);
        if ((cur & mask) != 0) return;
        if (!_mem.Writable(addr, 1)) return;
        _mem.W8(addr, (byte)(cur | mask));
        ModLogger.LogDebug($"shadow blade: re-set the learned flag for Shadow Blade in party slot {rosterSlot} (job index {jobIdx}, ability slot {slotIdx1})");
    }
}
