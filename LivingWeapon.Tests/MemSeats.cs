using LivingWeapon;

namespace LivingWeapon.Tests;

/// <summary>
/// The ONE file that knows how to seat fake units into a FakeSparseMemory at the real
/// Offsets layout: SeatRoster writes a roster slot (level/brave/faith fingerprint +
/// both hands), SeatBand writes a band entry (weapon, fingerprint, grid position, HP,
/// scheduler CT). Suites keep their domain wrappers (KillTracker's array oracle +
/// dead-streak settling, ExtraTurn's writable marking) on top of these.
/// </summary>
internal static class MemSeats
{
    internal static void SeatRoster(FakeSparseMemory m, int slot, int lvl, int br, int fa,
                                    int rh, int lh = 0xFFFF, int oh = 0xFFFF, int nameId = 0, int sprite = 0)
    {
        long rb = Offsets.RosterBase + (long)slot * Offsets.RosterStride;
        m.U8s[rb + Offsets.RLevel] = (byte)lvl;
        m.U8s[rb + Offsets.RBrave] = (byte)br;
        m.U8s[rb + Offsets.RFaith] = (byte)fa;
        m.U16s[rb + Offsets.RRHand]   = (ushort)rh;
        m.U16s[rb + Offsets.RLHand]   = (ushort)lh;
        m.U16s[rb + Offsets.ROffHand] = (ushort)oh;
        m.U16s[rb + Offsets.RNameId]  = (ushort)nameId;   // default 0 == old unseeded-read behavior
        // LW-31 stage 3: SpriteSet byte (Offsets.RSprite == roster +0x00). Default 0 is well under
        // 0x80 (AttackRow.Policy.HumanSprite), matching every pre-existing call site's implicit
        // "ordinary human" assumption unaffected.
        m.U8s[rb + Offsets.RSprite]  = (byte)sprite;
    }

    /// <summary>Seed a band entry's frame nameId back-reference (Offsets.ANameId, band-entry-
    /// relative == frame +0x1FC). Test seam for Iai's identity-match release: the arm-time
    /// capture reads the ROSTER copy (SeatRoster's nameId), this seeds the FRAME copy the acting
    /// pointer's read observes.</summary>
    internal static void SeatFrameNameId(FakeSparseMemory m, int bandIdx, int nameId)
    {
        long e = Band.Entry(bandIdx);
        m.U16s[e + Offsets.ANameId] = (ushort)nameId;
    }

    internal static void SeatBand(FakeSparseMemory m, int bandIdx, int weapon, int lvl, int br, int fa,
                                  int gx, int gy, int hp = 100, int maxHp = 100, int ctTurn = 0,
                                  int speed = 0)
    {
        long e = Band.Entry(bandIdx);
        m.U16s[e + (Offsets.CWeapon - Offsets.BandEntry)] = (ushort)weapon;
        m.U8s[e + Offsets.ALevel]  = (byte)lvl;
        m.U8s[e + Offsets.ABrave]  = (byte)br;
        m.U8s[e + Offsets.AFaith]  = (byte)fa;
        m.U8s[e + Offsets.AGx]     = (byte)gx;
        m.U8s[e + Offsets.AGy]     = (byte)gy;
        m.U16s[e + Offsets.AHp]    = (ushort)hp;
        m.U16s[e + Offsets.AMaxHp] = (ushort)maxHp;
        m.U8s[e + Offsets.ACtTurn] = (byte)ctTurn;
        m.U8s[e + Offsets.ASpeed]  = (byte)speed;
    }

    /// <summary>Seed a band entry's job byte (Puppeteer.JobOff, band-entry-relative == combat
    /// +0x03). Separate one-liner (not folded into SeatBand's signature) so only the tests that
    /// need it, the LW-56 canonical-signature fingerprint rescue, pay for it.</summary>
    internal static void SeatBandJob(FakeSparseMemory m, int bandIdx, byte job)
    {
        long e = Band.Entry(bandIdx);
        m.U8s[e + Puppeteer.JobOff] = job;
    }
}
