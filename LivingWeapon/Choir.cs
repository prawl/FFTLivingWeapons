using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Warlock's Staff's "Choir" signature: while a +3 Warlock's Staff is held in the MAIN HAND
/// and its bearer is alive and on the field, the nearest Tuning.ChoirMaxBeneficiaries (2) LIVING
/// units -- the bearer (distance 0) and its single nearest ally -- within Chebyshev radius 1
/// (the 8 adjacent tiles incl. diagonals) get the Non-charge support bit (id 227, band +0x7F mask
/// 0x04) OR-set each tick, so their magick casts instantly. The cap is a duet, not a full-party
/// blowout: a third adjacent ally gets nothing until a closer beneficiary leaves.
///
/// POSITIONAL AURA WITH DETERMINISTIC REVERT: when an ally leaves the radius, dies, or the
/// bearer dies/unequips/battle ends, the bit Choir set is CLEARED. Choir NEVER touches a unit
/// whose OWN picked support is Non-charge (id 227) -- the _granted set tracks only the units
/// Choir itself is responsible for.
///
/// PER-TICK HOLD: the engine normalizes the support field per turn, so the bit must be
/// re-OR-set each tick. The hold is idempotent (LarcenyPolicy.SetBit skips already-set bits).
///
/// BAND-TWIN SAFE: the band loop iterates every slot and acts per-entry address. An in-aura
/// twin gets SetBit; an out-of-aura twin gets ClearBit (if fp is in _granted). _granted is
/// NOT modified inside the per-entry loop, so each twin is handled correctly by its own address.
///
/// ALL WRITES ARE GUARDED: SetBit and ClearBit pre-check Readable+Writable. An unaddressable
/// byte is a silent no-op -- never a raw deref.
/// </summary>
internal sealed partial class Choir : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.OnField);

    private const int WarlockStaffId = 60;

    private readonly IGameMemory _mem;
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly List<int> _hands = new();
    private bool _wasActive;

    // Fingerprints of units whose Non-charge bit Choir granted this battle (NOT units who picked
    // it themselves -- those are skipped entirely). Used for per-entry revert on leave/death.
    private readonly HashSet<(int mhp, int lvl, int br, int fa)> _granted = new();

    // Non-charge bit encoding, resolved once at construction (id 227, base 198 -> byte 3, mask 0x04)
    private readonly int _ncByteOff;
    private readonly byte _ncMask;

    public Choir(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, IGameMemory? mem = null)
    {
        _mem = mem ?? new LiveMemory();
        _meta = meta;
        _kills = kills;
        Signatures.SupportBit(Tuning.InstantCastSupportId, out _ncByteOff, out _ncMask);
    }

    public void ResetBattle()
    {
        _granted.Clear();
        _wasActive = false;
        // No bit restore -- the per-battle combat struct is rebuilt; Sanctuary precedent.
    }

    public void Tick(bool onField)
    {
        if (!onField) return;
        if (!_meta.TryGetValue(WarlockStaffId, out var m) || m.Signature is null) return;

        int tier = Tuning.TierOf(_kills, WarlockStaffId);

        // Resolve the single roster slot holding id 60 in main hand; ambiguity = inactive.
        bool resolved = Wielder.TryResolveMainHand(_mem, WarlockStaffId, out var fp, _hands);
        long bearer = resolved ? Wielder.Locate(_mem, WarlockStaffId, _hands, fp) : 0;
        bool bearerAlive = bearer != 0 && _mem.U16(bearer + Offsets.AHp) > 0;
        bool active = IsActive(m.Signature, tier) && bearerAlive;

        if (active != _wasActive)
        {
            _wasActive = active;
            Log.Info(active
                ? "choir ACTIVE -- Warlock's Staff at +3 and its bearer lives; adjacent allies cast magick instantly"
                : "choir inactive -- the bearer is down or unequipped; instant-cast lifted");
        }

        if (!active)
        {
            // REVERT ALL: clear the Non-charge bit on every band entry Choir owns
            for (int s = 0; s < Offsets.BandSlots; s++)
            {
                long e = Band.Entry(s);
                if (!Band.IsValid(_mem, e)) continue;
                int mhp = _mem.U16(e + Offsets.AMaxHp);
                int lvl = _mem.U8(e + Offsets.ALevel);
                int br  = _mem.U8(e + Offsets.ABrave);
                int fa  = _mem.U8(e + Offsets.AFaith);
                if (_granted.Contains((mhp, lvl, br, fa)))
                    LarcenyPolicy.ClearBit(_mem, e, Offsets.ASupport + _ncByteOff, _ncMask);
            }
            _granted.Clear();
            return;
        }

        // Active: read aura center from the bearer's band entry
        int wgx    = _mem.U8(bearer + Offsets.AGx);
        int wgy    = _mem.U8(bearer + Offsets.AGy);
        int radius = m.Signature.InstantCastRadius;

        var allies = Band.AllyFingerprints(_mem);

        // Build the set of units who picked Non-charge themselves (never touch their bit)
        var protectedBF = new HashSet<(int br, int fa)>();
        for (int r = 0; r < Offsets.RosterSlots; r++)
        {
            long rb  = Offsets.RosterBase + (long)r * Offsets.RosterStride;
            int  lvl = _mem.U8(rb + Offsets.RLevel);
            if (lvl < 1 || lvl > 99) continue;
            if (_mem.U8(rb + Offsets.RSupport) == Tuning.InstantCastSupportId)
                protectedBF.Add((_mem.U8(rb + Offsets.RBrave), _mem.U8(rb + Offsets.RFaith)));
        }

        // Pass 1: gather every valid, ally, non-protected band entry; flag the in-aura LIVING ones as
        // cap candidates (each with its distance). The bearer is its own ally at distance 0.
        var entries = new List<(long e, (int mhp, int lvl, int br, int fa) fp, bool candidate)>();
        var candidates = new List<((int mhp, int lvl, int br, int fa) fp, int dist)>();
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (!Band.IsValid(_mem, e)) continue;

            int mhp = _mem.U16(e + Offsets.AMaxHp);
            int lvl = _mem.U8(e + Offsets.ALevel);
            int br  = _mem.U8(e + Offsets.ABrave);
            int fa  = _mem.U8(e + Offsets.AFaith);
            var entryFp = (mhp, lvl, br, fa);

            if (!allies.Contains(entryFp)) continue;          // positive ally match only
            if (protectedBF.Contains((br, fa))) continue;     // player's own Non-charge pick -- never touch

            int hp   = _mem.U16(e + Offsets.AHp);
            int dist = Chebyshev(wgx, wgy, _mem.U8(e + Offsets.AGx), _mem.U8(e + Offsets.AGy));
            bool candidate = hp > 0 && dist <= radius;
            entries.Add((e, entryFp, candidate));
            if (candidate) candidates.Add((entryFp, dist));
        }

        // Cap: at most ChoirMaxBeneficiaries nearest UNIQUE units win the grant (a duet, not a choir).
        var winners = SelectNearest(candidates, Tuning.ChoirMaxBeneficiaries);

        // Pass 2: set the bit on each WINNING in-aura entry; clear any entry Choir previously owned
        // that is no longer a winning in-aura entry (left the radius, died, or got bumped by a nearer
        // ally). Per-ENTRY, so band twins / fingerprint collisions resolve by their own address.
        foreach (var (e, efp, candidate) in entries)
        {
            if (candidate && winners.Contains(efp))
                LarcenyPolicy.SetBit(_mem, e, Offsets.ASupport + _ncByteOff, _ncMask);
            else if (_granted.Contains(efp))
                LarcenyPolicy.ClearBit(_mem, e, Offsets.ASupport + _ncByteOff, _ncMask);
        }
        _granted.Clear();
        _granted.UnionWith(winners);
    }
}
