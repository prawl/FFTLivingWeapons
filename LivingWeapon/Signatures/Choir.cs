using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Warlock's Staff "Choir" signature, holder only. While a +3 Warlock's Staff is in the main hand and a bearer
/// is alive on the field, each bearer gets the Non-charge support bit (id 227, band +0x7F mask 0x04) OR-set per
/// tick so magick casts instantly. The set is address-direct on entries from Wielder.ResolveDeployedMainHandAll.
/// _granted is keyed by the band-read fingerprint (mhp, lvl, br, fa), not the roster one, so the clear path
/// still matches after a mid-battle level-up drifts the band level. A unit whose own roster support is
/// InstantCastSupportId is never set or cleared. SetBit and ClearBit pre-check Readable and Writable.
/// </summary>
internal sealed partial class Choir : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.OnField);

    private const int WarlockStaffId = 60;

    private readonly IGameMemory _mem;
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    // LW-252 stage 5. The bearer tuple carries nameId; Choir ignores it since _granted is band-fp keyed.
    private readonly List<(long entry, (int lvl, int br, int fa) fp, int nameId)> _bearers = new();
    private bool _wasActive;

    // Band-read fingerprints of bearers whose Non-charge bit Choir set this battle.
    private readonly HashSet<(int mhp, int lvl, int br, int fa)> _granted = new();

    // Non-charge bit encoding (id 227, base 198 -> byte 3, mask 0x04), resolved at construction
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
        // No bit restore, the combat struct is rebuilt each battle.
    }

    public void Tick(bool onField)
    {
        if (!onField) return;
        if (!_meta.TryGetValue(WarlockStaffId, out var m) || m.Signature is null) return;

        int tier = Tuning.TierOf(_kills, WarlockStaffId);

        Wielder.ResolveDeployedMainHandAll(_mem, WarlockStaffId, _bearers);
        int aliveBearers = 0;
        foreach (var (entry, _, _) in _bearers)
            if (_mem.U16(entry + Offsets.AHp) > 0) aliveBearers++;

        bool active = IsActive(m.Signature, tier) && aliveBearers > 0;

        if (ActivationEdge.Step(ref _wasActive, active))
        {
            ModLogger.Event(LogVerb.Signature, active
                ? "Warlock's Staff at tier three is armed and its bearer lives; the bearer casts magick instantly."
                : "The Warlock's Staff's bearer is down or unequipped; instant casting ends.");
        }

        if (!active)
        {
            // Inactive. Clear the bit on every band entry Choir granted
            for (int s = 0; s < Offsets.BandSlots; s++)
            {
                long e = Band.Entry(s);
                if (!Band.IsValid(_mem, e)) continue;
                int mhp = _mem.U16(e + Offsets.AMaxHp);
                int lvl = _mem.U8(e + Offsets.ALevel);
                int br = _mem.U8(e + Offsets.ABrave);
                int fa = _mem.U8(e + Offsets.AFaith);
                if (_granted.Contains((mhp, lvl, br, fa)))
                    LarcenyPolicy.ClearBit(_mem, e, Offsets.ASupport + _ncByteOff, _ncMask);
            }
            _granted.Clear();
            return;
        }

        // Units who picked Non-charge themselves are left alone.
        var protectedBF = new HashSet<(int br, int fa)>();
        for (int r = 0; r < Offsets.RosterSlots; r++)
        {
            // LW-149 stage D. Uses Wielder's shared strict occupied-slot walk (Wielder.Roster.cs).
            if (!Wielder.TryOccupiedSlot(_mem, r, out long rb, out _)) continue;
            if (_mem.U8(rb + Offsets.RSupport) == Tuning.InstantCastSupportId)
                protectedBF.Add((_mem.U8(rb + Offsets.RBrave), _mem.U8(rb + Offsets.RFaith)));
        }

        // Set the bit on each live bearer without their own Non-charge, recording the band-read fingerprint.
        var winners = new HashSet<(int mhp, int lvl, int br, int fa)>();
        foreach (var (entry, fp, _) in _bearers)
        {
            if (_mem.U16(entry + Offsets.AHp) <= 0) continue;            // dead bearer doesn't project
            if (protectedBF.Contains((fp.br, fp.fa))) continue;          // self-picked Non-charge -> leave to them
            int mhp = _mem.U16(entry + Offsets.AMaxHp);
            int lvl = _mem.U8(entry + Offsets.ALevel);
            int br = _mem.U8(entry + Offsets.ABrave);
            int fa = _mem.U8(entry + Offsets.AFaith);
            LarcenyPolicy.SetBit(_mem, entry, Offsets.ASupport + _ncByteOff, _ncMask);
            winners.Add((mhp, lvl, br, fa));
        }

        // Clear bits on band entries Choir granted last tick that are no longer winners.
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (!Band.IsValid(_mem, e)) continue;
            int mhp = _mem.U16(e + Offsets.AMaxHp);
            int lvl = _mem.U8(e + Offsets.ALevel);
            int br = _mem.U8(e + Offsets.ABrave);
            int fa = _mem.U8(e + Offsets.AFaith);
            if (protectedBF.Contains((br, fa))) continue;     // the player's own Non-charge, leave it
            var fp4 = (mhp, lvl, br, fa);
            if (winners.Contains(fp4)) continue;              // still a winner (already set address-direct)
            if (_granted.Contains(fp4))
                LarcenyPolicy.ClearBit(_mem, e, Offsets.ASupport + _ncByteOff, _ncMask);
        }
        _granted.Clear();
        _granted.UnionWith(winners);
    }
}
