using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Warlock's Staff's "Choir" signature: each DEPLOYED bearer projects its own duet (bearer +
/// nearest ally); auras union. While a +3 Warlock's Staff is held in the MAIN HAND and at least
/// one bearer is alive and on the field, each live bearer independently grants the Non-charge
/// support bit (id 227, band +0x7F mask 0x04) to the nearest Tuning.ChoirMaxBeneficiaries (2)
/// LIVING units within Chebyshev radius 1 -- itself at distance 0 plus its single nearest ally.
/// Winners from all bearers are unioned, so two staves grant up to four instant-cast units.
/// A benched copy (no band entry) neither projects nor blocks a deployed bearer.
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
    private readonly List<(long entry, (int lvl, int br, int fa) fp)> _bearers = new();
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

        // Collect every deployed main-hand bearer; filter to those alive (a dead bearer must not project).
        Wielder.ResolveDeployedMainHandAll(_mem, WarlockStaffId, _bearers);
        var centers = new List<(int gx, int gy)>();
        foreach (var (entry, _) in _bearers)
            if (_mem.U16(entry + Offsets.AHp) > 0)
                centers.Add((_mem.U8(entry + Offsets.AGx), _mem.U8(entry + Offsets.AGy)));

        bool active = IsActive(m.Signature, tier) && centers.Count > 0;

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

        // Gather every valid, ally, non-protected band entry ONCE. Each entry records its own
        // position and HP for the per-bearer distance calculations below.
        var entries = new List<(long e, (int mhp, int lvl, int br, int fa) fp4, int gx, int gy, int hp)>();
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (!Band.IsValid(_mem, e)) continue;

            int mhp = _mem.U16(e + Offsets.AMaxHp);
            int lvl = _mem.U8(e + Offsets.ALevel);
            int br  = _mem.U8(e + Offsets.ABrave);
            int fa  = _mem.U8(e + Offsets.AFaith);
            var fp4 = (mhp, lvl, br, fa);

            if (!allies.Contains(fp4)) continue;           // positive ally match only
            if (protectedBF.Contains((br, fa))) continue;  // player's own Non-charge pick -- never touch

            int hp = _mem.U16(e + Offsets.AHp);
            int gx = _mem.U8(e + Offsets.AGx);
            int gy = _mem.U8(e + Offsets.AGy);
            entries.Add((e, fp4, gx, gy, hp));
        }

        // Winners = UNION of each bearer's per-bearer cap (ChoirMaxBeneficiaries nearest LIVING units).
        // A bearer is always distance 0 from itself and wins its own slot first (test 6 / spec pin (a)).
        var winners = new HashSet<(int mhp, int lvl, int br, int fa)>();
        foreach (var center in centers)
        {
            var cand = new List<((int mhp, int lvl, int br, int fa) fp, int dist)>();
            foreach (var (_, fp4, gx, gy, hp) in entries)
            {
                if (hp <= 0) continue;
                int dist = Chebyshev(center.gx, center.gy, gx, gy);
                if (dist <= radius) cand.Add((fp4, dist));
            }
            winners.UnionWith(SelectNearest(cand, Tuning.ChoirMaxBeneficiaries));
        }

        // Pass 2: per-ENTRY -- set bit on winning in-aura entries; clear stale Choir-owned entries.
        // inAuraOfAny is recomputed per entry against ALL live centers (spec pin (b)).
        foreach (var (e, fp4, gx, gy, hp) in entries)
        {
            bool inAuraOfAny = hp > 0 && IsInAnyCenter(centers, gx, gy, radius);
            if (inAuraOfAny && winners.Contains(fp4))
                LarcenyPolicy.SetBit(_mem, e, Offsets.ASupport + _ncByteOff, _ncMask);
            else if (_granted.Contains(fp4))
                LarcenyPolicy.ClearBit(_mem, e, Offsets.ASupport + _ncByteOff, _ncMask);
        }
        _granted.Clear();
        _granted.UnionWith(winners);
    }

    private static bool IsInAnyCenter(List<(int gx, int gy)> centers, int gx, int gy, int radius)
    {
        foreach (var c in centers)
            if (Chebyshev(c.gx, c.gy, gx, gy) <= radius) return true;
        return false;
    }
}
