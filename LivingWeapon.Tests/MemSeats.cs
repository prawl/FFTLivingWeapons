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
                                    int rh, int lh = 0xFFFF, int oh = 0xFFFF)
    {
        long rb = Offsets.RosterBase + (long)slot * Offsets.RosterStride;
        m.U8s[rb + Offsets.RLevel] = (byte)lvl;
        m.U8s[rb + Offsets.RBrave] = (byte)br;
        m.U8s[rb + Offsets.RFaith] = (byte)fa;
        m.U16s[rb + Offsets.RRHand]   = (ushort)rh;
        m.U16s[rb + Offsets.RLHand]   = (ushort)lh;
        m.U16s[rb + Offsets.ROffHand] = (ushort)oh;
    }

    internal static void SeatBand(FakeSparseMemory m, int bandIdx, int weapon, int lvl, int br, int fa,
                                  int gx, int gy, int hp = 100, int maxHp = 100, int ctTurn = 0)
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
    }
}
