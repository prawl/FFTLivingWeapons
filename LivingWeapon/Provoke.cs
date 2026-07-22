using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Defender's "Provoke" signature -- LW-123 arc 1. Modeled on ShadowBlade.cs (the closest existing
/// pattern): grant a JobCommand-injected command to a tier-3 main-hand Defender wielder, reusing
/// the proven Barrage/ShadowBlade injection primitives (TryResolveGrant/FindEmptySlot/InjectSlot/
/// ReleaseSlot/BarrageState) and the shared Squire/Knight whitelist (ProvokePolicy delegates to
/// ShadowBladePolicy -- see that class's doc for why). Split across two partials, a real seam
/// (not a line-count dodge): this file is the SLOT lifecycle (JobCommand injection, exactly
/// ShadowBlade's shape) plus the orchestrating Tick(); Provoke.Table.cs is the TABLE lifecycle
/// (the two byte writes that repoint ability 189's effect), a genuinely separate state machine
/// with its own arming condition -- see the class doc there for why the two cannot share one.
///
/// SHIPS INERT. items.json id 33 (Defender) carries no `signature` block yet, so in production
/// this module is constructed, ticks, finds no meta.Signature, and does nothing. That is
/// deliberate: the mark this module makes castable NEVER EXPIRES and CANNOT BE RE-APPLIED (see
/// docs/PROVOKE_AC.md), and the hold engine that would make the mark do anything -- and clear it
/// again -- is a separate arc that does not exist yet. Tests supply their own meta dictionary
/// (exactly like ShadowBladeTests), so this module is fully covered without touching
/// data/items.json.
///
/// The wielder scan below does NOT filter by job eligibility (unlike ShadowBlade's scan, where
/// IsEligible and TryResolveGrant are the same check): it finds any valid Defender main-hand
/// wielder, and eligibility is resolved separately afterward, only for the slot half -- see
/// Provoke.Table.cs's class doc for why the table half must stay job-agnostic.
/// </summary>
internal sealed partial class Provoke : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick();
    internal const int DefenderId = 33;

    private readonly IGameMemory _mem;
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly BarrageState _state = new();   // slot lifecycle: same atomic (RecId, SlotIdx) ledger Barrage/ShadowBlade use
    private bool _wasActive;
    private int _lastUnsupportedJob = -1;
    private bool _noSlotLogged;   // Signatures.StuckEdge latch: no empty slot nag

    public Provoke(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, IGameMemory? mem = null)
    {
        _mem = mem ?? new LiveMemory();
        _meta = meta;
        _kills = kills;
    }

    public void ResetBattle()
    {
        _wasActive = false;   // session-long grant; the injected slot (BarrageState) persists across battles
        _noSlotLogged = false;
        // The table repoint is ALSO session-long (keyed on wielder existence every tick, not on
        // battle edges -- Provoke ticks pre-gate, in and out of battle like Barrage/ShadowBlade),
        // so nothing here touches the table half's bookkeeping.
    }

    public void Tick()
    {
        if (!_meta.TryGetValue(DefenderId, out var m) || m.Signature is null) return;
        int tier = Tuning.TierOf(_kills, DefenderId);
        int abilityId = m.Signature.GrantCommandAbilityId;
        bool active = ProvokePolicy.IsActive(m.Signature, tier);

        // Find ANY tier-3 main-hand Defender wielder -- job-agnostic (see class doc).
        int wielderSlot = -1, wielderJob = -1, wielderSecondary = -1;
        if (active)
        {
            for (int r = 0; r < Offsets.RosterSlots; r++)
            {
                long rb = Offsets.RosterBase + (long)r * Offsets.RosterStride;
                if (!_mem.Readable(rb + Offsets.RNameId, 2)) continue;
                int lvl = _mem.U8(rb + Offsets.RLevel);
                if (lvl < 1 || lvl > 99) continue;
                if (_mem.U16(rb + Offsets.RRHand) != DefenderId) continue;   // main hand only
                int jobId = _mem.U8(rb + Barrage.RJobId);
                if (jobId <= 0) continue;
                wielderSlot = r;
                wielderJob = jobId;
                wielderSecondary = _mem.U8(rb + Barrage.RSecondary);
                break;
            }
        }

        // TABLE LIFECYCLE: independent of job eligibility, see Provoke.Table.cs's class doc.
        if (wielderSlot >= 0) WriteTable(abilityId); else RestoreTable(abilityId);

        // SLOT LIFECYCLE: same shape as ShadowBlade.
        if (wielderSlot < 0)
        {
            if (_wasActive) ModLogger.Event(LogVerb.Grant, "Provoke is no longer granted; no Defender wielder remains");
            _wasActive = false;
            RestoreSlot(abilityId);
            return;
        }

        if (!ProvokePolicy.TryResolveGrant(wielderJob, wielderSecondary, out int recId, out int jobIdx, out bool viaSecondary))
        {
            if (_lastUnsupportedJob != wielderJob)
            {
                _lastUnsupportedJob = wielderJob;
                ModLogger.WarnWithTrace(LogVerb.Grant,
                    $"{LogNames.Job(wielderJob)} cannot receive Provoke; it needs Squire or Knight as the primary job, or one of their action sets as the secondary command",
                    $"provoke ungrantable (job {wielderJob}, secondary command {wielderSecondary})");
            }
            RestoreSlot(abilityId);
            _wasActive = false;
            return;
        }

        if (!_wasActive)
        {
            _wasActive = true;
            ModLogger.EventWithTrace(LogVerb.Grant,
                $"The Defender wielder now has Provoke in {LogNames.Job(wielderJob)}'s action list{(viaSecondary ? " (via their secondary command)" : "")}",
                $"provoke grant (party slot {wielderSlot}, ability {abilityId}, record {recId}, learn index {jobIdx})");
        }

        // Job changed mid-session: release the old record's slot before targeting the new one.
        if (_state.RecId >= 0 && _state.RecId != recId) RestoreSlot(abilityId);

        long flagAddr = Barrage.AbilityBase + (long)recId * Barrage.RecSize - Barrage.FlagPrefixSize;
        long abBase = Barrage.AbilityBase + (long)recId * Barrage.RecSize;
        if (!_mem.Readable(flagAddr, Barrage.RecSize)) return;

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
                if (Signatures.StuckEdge(ref _noSlotLogged, true))
                    ModLogger.Debug(LogVerb.Grant, $"no empty ability slot in the {LogNames.Job(wielderJob)} command list (record {recId}); cannot inject Provoke");
                return;
            }
            Signatures.StuckEdge(ref _noSlotLogged, false);
            slotIdx = slot1 - 1;
            _state.Set(recId, slotIdx);
            ModLogger.Debug(LogVerb.Grant, $"placed Provoke in ability slot {slot1} of {LogNames.Job(wielderJob)}'s action list (record {recId})");
        }

        if (!_mem.TryReadBytes(flagAddr, Barrage.RecSize, out byte[] cur)) return;
        ushort curExt = (ushort)(cur[0] | (cur[1] << 8));
        byte curByte = cur[Barrage.FlagPrefixSize + slotIdx];
        if (ShadowBladePolicy.NeedsInject(curByte, curExt, slotIdx, abilityId))
            Barrage.InjectSlot(_mem, flagAddr, abBase, slotIdx, abilityId);

        HoldLearnedBit(wielderSlot, jobIdx, slotIdx + 1);
    }

    /// <summary>Release our injected slot (if any) and forget it, unconditionally -- the slot
    /// lifecycle only. See ShadowBlade.Restore (same shape) for why clearing happens even when the
    /// release below is refused.</summary>
    private void RestoreSlot(int abilityId)
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
                    "Provoke was removed from the job's action list; the rest of the list is untouched",
                    $"provoke release (record {recId}, slot {slotIdx})");
            else
                ModLogger.WarnWithTrace(LogVerb.Grant,
                    "Could not remove Provoke from its slot; something else changed it first",
                    $"provoke release refused (record {recId}, slot {slotIdx})");
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
        ModLogger.Debug(LogVerb.Grant, $"re-set the learned flag for Provoke in party slot {rosterSlot} (job index {jobIdx}, ability slot {slotIdx1})");
    }
}
