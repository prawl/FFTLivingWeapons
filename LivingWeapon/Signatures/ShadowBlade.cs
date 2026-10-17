using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Sanguine Sword's "Shadow Blade" signature. Grants the wielder Gaffgarion's Shadow Blade (a ranged
/// dark strike that absorbs HP) through JobCommand injection, reusing Barrage's primitives and
/// BarrageState. It differs from Barrage in three ways.
///   - The ability id comes from meta (GrantCommandAbilityId), not a hardcoded 358.
///   - Eligibility is a whitelist of Squire/Knight (records 5/7), reached through the primary job or a
///     mounted secondary command (ShadowBladePolicy.TryResolveGrant). Barrage is Thief-only.
///   - The idempotent inject check works for any id (ShadowBladePolicy.NeedsInject).
///
/// The find/inject/release/hold flow is structurally Barrage's. Once Shadow Blade is verified, extract
/// the shared core, so the shipped Barrage path is not refactored blind.
///
/// Still pending a deploy. Does it render with its real menu name, and does every resolvable job
/// execute it? Release clears only the slot we injected (checked to still hold our ability), so a
/// wrong guess reverts cleanly. Records 5 and 7 are shared with Barrage (Steal as a secondary, see
/// Barrage.Policy.cs) and with the planned Provoke, so each module writes and releases only its own
/// slot and the grants cannot disturb each other or an ability the player owns in another slot.
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
    private bool _noSlotLogged;   // Signatures.StuckEdge latch. No empty slot nag

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
            // Eligibility is open, so the next grant may resolve to a different record. Restore() releases
            // the tracked record's slot (BarrageState.RecId) and always clears.
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

        // Job changed mid-session. Release the old record's slot before targeting the new one.
        if (_state.RecId >= 0 && _state.RecId != recId) Restore(abilityId);

        long flagAddr = Barrage.AbilityBase + (long)recId * Barrage.RecSize - Barrage.FlagPrefixSize;
        long abBase = Barrage.AbilityBase + (long)recId * Barrage.RecSize;
        if (!_mem.Readable(flagAddr, Barrage.RecSize)) { return; }

        // Only trust a remembered slot found in this record (same check as Barrage.cs), even though
        // Restore() above should already have cleared a mismatched one.
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
                // Debug tier + latch. The condition can hold every tick until a slot frees up.
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

    /// <summary>Release our injected slot (if any) and forget it, whenever the grant ends or the job changes
    /// records. <paramref name="abilityId"/> is passed in from Tick's meta read so a missing key cannot
    /// no-op it. State is cleared even if the release is refused (see Barrage.Restore, BarrageState.Clear).</summary>
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
