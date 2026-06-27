using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Warlock's Staff's "Choir" signature -- HOLDER-ONLY. While a +3 Warlock's Staff is held
/// in the MAIN HAND and at least one bearer is alive and on the field, each live bearer gets
/// the Non-charge support bit (id 227, band +0x7F mask 0x04) OR-set per tick so their magick
/// casts instantly. SET is ADDRESS-DIRECT onto the entry returned by
/// Wielder.ResolveDeployedMainHandAll -- no adjacent-ally aura.
///
/// BAND-KEYED _granted: the fingerprint stored in _granted is READ FROM THE BAND ENTRY
/// (mhp, lvl, br, fa via _mem reads on `entry`), NOT from the roster fp tuple. This ensures
/// the clear path matches later band scans even after a mid-battle level-up (roster lvl stays
/// pre-battle; band lvl drifts up). A roster-keyed _granted would miss the drifted band fp
/// and leave the bit stuck on an unequipped bearer.
///
/// CLEAR PATH: band scan checks _granted (band-read fp). Stale entries (bearer dropped, died,
/// below tier, battle ended) are cleared via ClearBit on each matching band entry.
///
/// PROTECTED UNITS: a unit whose OWN roster support == InstantCastSupportId is never set or
/// cleared -- their innate bit is theirs; it is excluded from winners so _granted never owns it.
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

    // Fingerprints (band-read: mhp,lvl,br,fa) of bearer band entries whose Non-charge bit
    // Choir granted this battle. Used for per-entry revert on unequip/death/battle-end.
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

        // Every deployed main-hand bearer; a dead bearer must not project.
        Wielder.ResolveDeployedMainHandAll(_mem, WarlockStaffId, _bearers);
        int aliveBearers = 0;
        foreach (var (entry, _) in _bearers)
            if (_mem.U16(entry + Offsets.AHp) > 0) aliveBearers++;

        bool active = IsActive(m.Signature, tier) && aliveBearers > 0;

        if (active != _wasActive)
        {
            _wasActive = active;
            Log.Info(active
                ? "choir ACTIVE -- Warlock's Staff at +3 and its bearer lives; the bearer casts magick instantly"
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

        // Units who picked Non-charge themselves -> never touch their bit.
        var protectedBF = new HashSet<(int br, int fa)>();
        for (int r = 0; r < Offsets.RosterSlots; r++)
        {
            long rb  = Offsets.RosterBase + (long)r * Offsets.RosterStride;
            int  lvl = _mem.U8(rb + Offsets.RLevel);
            if (lvl < 1 || lvl > 99) continue;
            if (_mem.U8(rb + Offsets.RSupport) == Tuning.InstantCastSupportId)
                protectedBF.Add((_mem.U8(rb + Offsets.RBrave), _mem.U8(rb + Offsets.RFaith)));
        }

        // HOLDER-ONLY: SET address-direct on each alive, non-self-Non-charge bearer. Record the
        // band-read fingerprint (NOT the roster fp.lvl) so the clear path matches later band scans
        // even after a mid-battle level-up (band lvl drifts up; roster lvl stays pre-battle).
        var winners = new HashSet<(int mhp, int lvl, int br, int fa)>();
        foreach (var (entry, fp) in _bearers)
        {
            if (_mem.U16(entry + Offsets.AHp) <= 0) continue;            // dead bearer doesn't project
            if (protectedBF.Contains((fp.br, fp.fa))) continue;          // self-picked Non-charge -> leave to them
            int mhp = _mem.U16(entry + Offsets.AMaxHp);
            int lvl = _mem.U8(entry + Offsets.ALevel);
            int br  = _mem.U8(entry + Offsets.ABrave);
            int fa  = _mem.U8(entry + Offsets.AFaith);
            LarcenyPolicy.SetBit(_mem, entry, Offsets.ASupport + _ncByteOff, _ncMask);
            winners.Add((mhp, lvl, br, fa));
        }

        // CLEAR stale: band entries Choir owned last tick that are no longer winners.
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (!Band.IsValid(_mem, e)) continue;
            int mhp = _mem.U16(e + Offsets.AMaxHp);
            int lvl = _mem.U8(e + Offsets.ALevel);
            int br  = _mem.U8(e + Offsets.ABrave);
            int fa  = _mem.U8(e + Offsets.AFaith);
            if (protectedBF.Contains((br, fa))) continue;     // player's own Non-charge -- never touch
            var fp4 = (mhp, lvl, br, fa);
            if (winners.Contains(fp4)) continue;              // still a winner (already set address-direct)
            if (_granted.Contains(fp4))
                LarcenyPolicy.ClearBit(_mem, e, Offsets.ASupport + _ncByteOff, _ncMask);
        }
        _granted.Clear();
        _granted.UnionWith(winners);
    }
}
