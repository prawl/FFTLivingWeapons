using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Sanguine Sword's "Shadow Blade" signature: grant the wielder Gaffgarion's Shadow Blade -- a ranged
/// dark strike that absorbs the foe's HP -- via JobCommand injection. It REUSES every proven Barrage
/// primitive (the table-layout consts + TryResolveGrant / FindEmptySlot / ExtendBit / SlotByte /
/// InjectSlot / ReleaseSlot / learned-bit math + BarrageState); the shipped Barrage module is not
/// touched. Only three things differ from Barrage and live here:
///   - the granted ability is read from meta (GrantCommandAbilityId), not hardcoded to 358;
///   - eligibility is a WHITELIST of sword skill-sets -- Squire/Knight (records 5/7) -- reached via
///     the wielder's primary job OR a mounted secondary command (ShadowBladePolicy.TryResolveGrant),
///     where Barrage is Thief-only; the resolution is otherwise identical;
///   - the idempotent-inject check is general for any id (ShadowBladePolicy.NeedsInject).
///
/// FOLLOW-UP SEAM: the find/inject/release/hold orchestration below is structurally Barrage's. Once
/// Shadow Blade is live-verified, the shared core should be extracted into one helper both call --
/// deferred deliberately, so a blind refactor can't regress the SHIPPED Barrage path.
///
/// LIVE-PENDING (only a deploy can settle, the same class Barrage needed): does Shadow Blade render
/// with its real menu NAME (the blank-name risk -- it has a table name, so it SHOULD), and does
/// every resolvable job actually EXECUTE it (the special-executor swallow)? All reads/writes are
/// VirtualQuery-guarded. Ending the grant releases ONLY the one slot we injected (verified at
/// release time to still hold our ability), so a wrong guess reverts cleanly without touching
/// anything else in the record -- see ReleaseSlot.
///
/// SHARED RECORD, NOW SAFE: the grant can target either whitelisted record 5 or 7, the SAME two
/// Barrage can land in when a non-Thief primary has Steal mounted as a secondary (see the
/// TryResolveGrant correction in Barrage.Policy.cs), and Provoke (a forthcoming Knight-sword
/// signature) is slated for record 7 too. Slot-scoped release is what makes that safe: each module
/// only ever writes and releases its OWN slot, so two grants sharing a record can never see or
/// disturb each other's -- and neither can drop an ability the player (or another mod) legitimately
/// owns in a different slot of that same record.
/// </summary>
internal sealed class ShadowBlade : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick();
    private const int SanguineId = 23;

    private readonly IGameMemory _mem;
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly BarrageState _state = new();   // same atomic (RecId, SlotIdx) ledger Barrage uses
    private bool _wasActive;
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
        _wasActive = false;   // session-long grant; the injected slot (BarrageState) persists across battles
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
            if (_wasActive) ModLogger.Event(LogVerb.Grant, "Shadow Blade is no longer granted; no eligible Sanguine Sword wielder remains");
            _wasActive = false;
            // Eligibility is OPEN (unlike Barrage's fixed Thief path), so the NEXT grant can resolve
            // to a DIFFERENT record -- Restore() releases the slot in the record we're actually
            // tracking (BarrageState.RecId) and always clears, so nothing stale survives to be
            // misapplied against whichever record comes next.
            Restore(abilityId);
            return;
        }

        if (!ShadowBladePolicy.TryResolveGrant(wielderJob, wielderSecondary, out int recId, out int jobIdx, out bool viaSecondary))
        {
            if (_lastUnsupportedJob != wielderJob)
            {
                _lastUnsupportedJob = wielderJob;
                ModLogger.WarnWithTrace(LogVerb.Grant,
                    $"{LogNames.Job(wielderJob)} cannot receive Shadow Blade; it needs Squire or Knight as the primary job, or one of their action sets as the secondary command",
                    $"shadow blade ungrantable (job {wielderJob}, secondary command {wielderSecondary})");
            }
            Restore(abilityId);
            _wasActive = false;
            return;
        }

        if (!_wasActive)
        {
            _wasActive = true;
            ModLogger.EventWithTrace(LogVerb.Grant,
                $"The Sanguine Sword wielder now has Shadow Blade in {LogNames.Job(wielderJob)}'s action list{(viaSecondary ? " (via their secondary command)" : "")}",
                $"shadow blade grant (party slot {wielderSlot}, ability {abilityId}, record {recId}, learn index {jobIdx})");
        }

        // Job changed mid-session: release the old record's slot before targeting the new one.
        if (_state.RecId >= 0 && _state.RecId != recId) Restore(abilityId);

        long flagAddr = Barrage.AbilityBase + (long)recId * Barrage.RecSize - Barrage.FlagPrefixSize;
        long abBase = Barrage.AbilityBase + (long)recId * Barrage.RecSize;
        if (!_mem.Readable(flagAddr, Barrage.RecSize)) { return; }

        // Find or verify the injection slot. Only trust a remembered slot when it was found in
        // THIS record -- see the matching comment in Barrage.cs for why this check survives even
        // though Restore() above should already have cleared a mismatched record.
        int slotIdx = _state.RecId == recId ? _state.SlotIdx : -1;
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
                    ModLogger.Debug(LogVerb.Grant, $"no empty ability slot in the {LogNames.Job(wielderJob)} command list (record {recId}); cannot inject Shadow Blade");
                return;
            }
            Signatures.StuckEdge(ref _noSlotLogged, false);   // slot found -> re-arm for next time
            slotIdx = slot1 - 1;
            _state.Set(recId, slotIdx);
            ModLogger.Debug(LogVerb.Grant, $"placed Shadow Blade in ability slot {slot1} of {LogNames.Job(wielderJob)}'s action list (record {recId})");
        }

        if (!_mem.TryReadBytes(flagAddr, Barrage.RecSize, out byte[] cur)) return;
        ushort curExt = (ushort)(cur[0] | (cur[1] << 8));
        byte curByte = cur[Barrage.FlagPrefixSize + slotIdx];
        if (ShadowBladePolicy.NeedsInject(curByte, curExt, slotIdx, abilityId))
            Barrage.InjectSlot(_mem, flagAddr, abBase, slotIdx, abilityId);

        HoldLearnedBit(wielderSlot, jobIdx, slotIdx + 1);
    }

    /// <summary>Release our injected slot (if any) and forget it, unconditionally -- called
    /// whenever the grant ends or the wielder's job changes records. <paramref name="abilityId"/>
    /// is passed in (read from meta at the top of Tick) rather than re-read here, so a missing meta
    /// key can never make this silently no-op the way a second dictionary lookup could. See
    /// Barrage.Restore (same shape, ability id hardcoded there) and BarrageState.Clear for why
    /// clearing happens unconditionally, even when the release below is refused.</summary>
    private void Restore(int abilityId)
    {
        int recId = _state.RecId;
        int slotIdx = _state.SlotIdx;
        if (recId >= 0 && slotIdx >= 0)
        {
            long flagAddr = Barrage.AbilityBase + (long)recId * Barrage.RecSize - Barrage.FlagPrefixSize;
            long abBase = Barrage.AbilityBase + (long)recId * Barrage.RecSize;
            bool released = Barrage.ReleaseSlot(_mem, flagAddr, abBase, slotIdx, abilityId);
            if (released)
                ModLogger.EventWithTrace(LogVerb.Grant,
                    "Shadow Blade was removed from the job's action list; the rest of the list is untouched",
                    $"shadow blade release (record {recId}, slot {slotIdx})");
            else
                ModLogger.WarnWithTrace(LogVerb.Grant,
                    "Could not remove Shadow Blade from its slot; something else changed it first",
                    $"shadow blade release refused (record {recId}, slot {slotIdx})");
        }
        _state.Clear();
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
        ModLogger.Debug(LogVerb.Grant, $"re-set the learned flag for Shadow Blade in party slot {rosterSlot} (job index {jobIdx}, ability slot {slotIdx1})");
    }
}
