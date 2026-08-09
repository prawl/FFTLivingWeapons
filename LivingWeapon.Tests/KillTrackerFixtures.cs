using LivingWeapon;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-160: the shared seeding-helper home for the nine suites that drive KillTracker.Poll
/// (KillTrackerTests, KillTrackerStampTests, KillTrackerDeedTests, KillCreditCoverageTests,
/// CounterAttributionTests, DelayedActorTests, CrossTurnSummonTests, SummonerAttributionTests,
/// VictimProbeTests). Those files each hand-copied this family under a documented per-file
/// mirroring convention; the owner retired that convention on 2026-08-08 and the copies folded
/// here, the same move BandFixtures made for the signature-module seeders (LW-149/LW-157).
///
/// Every body below is a verbatim copy of the pre-fold canonical (the KillTrackerTests lineage;
/// the DelayedActorTests-lineage variants differed only by omitted parameters and hardcoded
/// defaults, and every cross-variant call site passes those by name, so the canonical signatures
/// bind every existing call identically). KillTrackerFixturesTests.cs pins the exact
/// address/value sets against the pre-fold bodies, including the variant-vs-canonical SetEnemy
/// equivalence, so a fixture that drifts by one byte fails loud instead of silently widening a
/// test's premise.
///
/// Consumers import via `using static LivingWeapon.Tests.KillTrackerFixtures;` so call sites
/// read unchanged. Module-specific helpers stay in their own files on purpose: KillTrackerTests'
/// SetDeadBit and CrossTurnSummonTests' SetChargingBit have one consumer each, and the
/// resolver/latch/turn-tracker suites (ActorResolver*, ActedPeriodLatch, TurnTracker,
/// FlagOwnerResolve, ProvokeHold) seed the same addresses in their own domain shapes and are
/// deliberately outside this fold.
/// </summary>
internal static class KillTrackerFixtures
{
    /// <summary>The active (condensed) struct: which unit's turn it is, by HP/MaxHP/level.
    /// `acted` is the action-complete flag (0x14077CA8C): the latch only captures when it is 1,
    /// so the inter-turn flicker of the struct (acted=0) can't steal credit. `team` is TqTeam
    /// (0=player, 1=enemy, 2=ally/guest; any other value is treated as "unknown" and takes the
    /// normal credit path, fail-safe).</summary>
    public static void SetActive(FakeSparseMemory m, int hp, int maxHp, int level, int team = 0, int acted = 1)
    {
        m.U16s[Offsets.TurnQueue + Offsets.TqTeam] = (ushort)team;
        m.U16s[Offsets.TurnQueue + Offsets.TqHp] = (ushort)hp;
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = (ushort)maxHp;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = (ushort)level;
        m.U8s[Offsets.Acted] = (byte)acted;
    }

    /// <summary>Write a unit into the BAND entry at band slot <paramref name="slot"/>.
    /// This is the live source for corpse detection and actor resolution.
    /// Pass level/brave/faith to make it matchable as the actor (ActorResolver reads the band).
    /// Pass weapon to seat the actor's equipped item at band+AWeapon (used by the
    /// weapon-disambiguation path in FingerprintPlayer / MainHandFromRoster).</summary>
    public static void SetUnit(FakeSparseMemory m, int slot, int hp, int maxHp = 400, int gx = 5, int gy = 5,
                               int level = 10, int brave = 50, int faith = 50, int weapon = 0)
        => MemSeats.SeatBand(m, slot, weapon: weapon, lvl: level, br: brave, fa: faith,
                             gx: gx, gy: gy, hp: hp, maxHp: maxHp);

    /// <summary>Write identity fields into the STATIC ARRAY slot so the capture oracle can
    /// classify this as a known enemy. inb defaults to 1 but is NOT required by the capture --
    /// live, the flag pulses 0/1 per unit mid-battle (it is not a membership marker).</summary>
    public static void SetArrayEnemy(FakeSparseMemory m, int slot, int level, int brave, int faith, int maxHp,
                                     int inb = 1)
    {
        long s = Offsets.ArrayReadBase + (long)slot * Offsets.ArrayStride;
        m.U16s[s + Offsets.AInBattle] = (ushort)inb;
        m.U8s[s + Offsets.ALevel] = (byte)level;
        m.U8s[s + Offsets.ABrave] = (byte)brave;
        m.U8s[s + Offsets.AFaith] = (byte)faith;
        m.U16s[s + Offsets.AMaxHp] = (ushort)maxHp;
    }

    /// <summary>Convenience: write BOTH the band entry (liveness) and the static array slot
    /// (identity capture). Enemies in tests must have their identity captured to earn credit.</summary>
    public static void SetEnemy(FakeSparseMemory m, int slot, int hp, int maxHp = 400, int gx = 5, int gy = 5,
                                int level = 10, int brave = 50, int faith = 50)
    {
        SetUnit(m, slot, hp, maxHp, gx, gy, level, brave, faith);
        if (slot <= Offsets.EnemySlotMax)
            SetArrayEnemy(m, slot, level, brave, faith, maxHp);
    }

    /// <summary>A roster slot keyed by the (level,brave,faith) fingerprint -> its R-hand weapon.
    /// ROffHand (+0x18) is where the live dual-wield off-hand actually sits. nameId defaults to 0
    /// (old unseeded-read behavior); the register-path tests seed it explicitly to bridge a pointer
    /// arrival's frame nameId back to this slot.</summary>
    public static void SetRoster(FakeSparseMemory m, int slot, int level, int brave, int faith, int weapon,
                                 int lhand = 0xFFFF, int offhand = 0xFFFF, int nameId = 0)
        => MemSeats.SeatRoster(m, slot, level, brave, faith, weapon, lhand, offhand, nameId);

    /// <summary>Point Offsets.ActorPtr at band slot <paramref name="bandIdx"/>'s combat FRAME base
    /// (mirrors TurnTrackerTests.PointAt).</summary>
    public static void PointAt(FakeSparseMemory m, int bandIdx) =>
        m.SeedU64(Offsets.ActorPtr, (ulong)(Offsets.FrameReadBase + (long)bandIdx * Offsets.CombatStride));

    /// <summary>Bridge a band slot's frame nameId to a roster slot's nameId (MemSeats.SeatFrameNameId
    /// wrapper) so the register's roster bridge resolves that pointer arrival to a specific player.</summary>
    public static void SetFrameNameId(FakeSparseMemory m, int bandIdx, int nameId) =>
        MemSeats.SeatFrameNameId(m, bandIdx, nameId);

    /// <summary>Flip the Jump/charge commit bit (+0x45/0x04) at a band slot: OR-set on
    /// <paramref name="set"/>, AND-clear otherwise; neighbor bits preserved either way.</summary>
    public static void SetJumpBit(FakeSparseMemory m, int bandSlot, bool set = true)
    {
        long addr = Band.Entry(bandSlot);
        byte cur = m.U8s.TryGetValue(addr + Offsets.ADeadStatus, out var v) ? v : (byte)0;
        m.U8s[addr + Offsets.ADeadStatus] = set
            ? (byte)(cur | Offsets.AJumpBit)
            : (byte)(cur & ~Offsets.AJumpBit);
    }

    /// <summary>Poll n times with onField=true (builds alive/dead streaks).</summary>
    public static void Settle(KillTracker t, int n = 3) { for (int i = 0; i < n; i++) t.Poll(true); }

    /// <summary>Set a band slot alive (hp>0), settle 3 ticks (seenAlive), then set it dead and
    /// settle 3 ticks (deadStreak). Leaves it ready for credit.</summary>
    public static void AliveThenDead(FakeSparseMemory m, int slot, KillTracker t,
                                     int hp = 300, int maxHp = 400, int level = 10, int brave = 50, int faith = 50)
    {
        SetEnemy(m, slot, hp, maxHp, level: level, brave: brave, faith: faith);
        Settle(t);
        SetUnit(m, slot, hp: 0, maxHp: maxHp, level: level, brave: brave, faith: faith);
        Settle(t);
    }
}
