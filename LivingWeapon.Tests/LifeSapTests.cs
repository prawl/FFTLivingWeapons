using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Umbral Rod's "Life Sap" signature. At +3, a kill credited to the rod restores the wielder
/// by LifeSapPct (25%) of their max HP, clamped at full. The trigger is the per-weapon
/// kill-tally diff (the ExtraTurn freshKill pattern), so attribution rides the proven
/// KillTracker credit. The heal NEVER revives: a 0-HP wielder is left alone.
///
/// Pure jobs in LifeSap.Policy.cs:
///   (1) IsActive: gates on lifeSapOnKill AND tier >= AtTier.
///   (2) FreshKill: tally-diff with the -1 unprimed sentinel (no fire on first sight).
///   (3) HealAmount: round(maxHp * pct), floor 1 for any positive maxHp.
///   (4) NewHp: clamp at maxHp; hp <= 0 is untouched (never revive).
///   (5) WriteHp: guarded little-endian u16 write to the band entry's HP.
/// </summary>
public class LifeSapTests
{
    // Pinned buffers are committed addresses in our own process, so the production adapter's
    // RPM/WPM reads work on them for real -- the guard path is exercised, not faked.
    private static readonly LiveMemory Live = new();

    private static WeaponSignature SapSig(int atTier = 3) =>
        new() { AtTier = atTier, LifeSapOnKill = true, DisplayLabel = "Life Sap" };

    // ---- (1) IsActive ----

    [Fact]
    public void IsActive_false_when_no_signature()
        => Assert.False(LifeSap.IsActive(null, tier: 3));

    [Fact]
    public void IsActive_false_when_not_a_lifesap_weapon()
        => Assert.False(LifeSap.IsActive(new WeaponSignature { AtTier = 3 }, tier: 3));

    [Fact]
    public void IsActive_false_below_tier()
        => Assert.False(LifeSap.IsActive(SapSig(), tier: 2));

    [Fact]
    public void IsActive_true_at_and_above_tier()
    {
        Assert.True(LifeSap.IsActive(SapSig(), tier: 3));
        Assert.True(LifeSap.IsActive(SapSig(), tier: 4));
    }

    // ---- (2) FreshKill: the tally-diff trigger ----

    [Theory]
    [InlineData(-1, 0, false)]   // unprimed: first sight never fires
    [InlineData(-1, 5, false)]   // unprimed even with seeded kills
    [InlineData(0, 1, true)]
    [InlineData(5, 6, true)]
    [InlineData(5, 5, false)]    // no change
    [InlineData(5, 4, false)]    // tally can only climb; a drop is not a kill
    public void FreshKill_fires_only_on_a_primed_increment(int last, int now, bool expected)
        => Assert.Equal(expected, Signatures.FreshKill(last, now));

    // ---- (3) HealAmount: 25% of max, floor 1 ----

    [Theory]
    [InlineData(100, 25)]
    [InlineData(200, 50)]
    [InlineData(10, 3)]    // round(2.5) away from zero = 3
    [InlineData(2, 1)]     // round(0.5) away from zero = 1
    [InlineData(1, 1)]     // floor 1 (round(0.25) = 0 would be a dead grant)
    [InlineData(0, 0)]     // no maxHp -> no heal
    public void HealAmount_is_a_quarter_of_max_floor_1(int maxHp, int expected)
        => Assert.Equal(expected, LifeSap.HealAmount(maxHp, 0.25));

    // ---- (4) NewHp: clamp, never revive ----

    [Theory]
    [InlineData(50, 100, 25, 75)]
    [InlineData(90, 100, 25, 100)]   // clamped at full
    [InlineData(100, 100, 25, 100)]  // already full
    [InlineData(0, 100, 25, 0)]      // dead: NEVER revive
    [InlineData(1, 100, 25, 26)]
    public void NewHp_clamps_and_never_revives(int hp, int maxHp, int heal, int expected)
        => Assert.Equal(expected, LifeSap.NewHp(hp, maxHp, heal));

    // ---- (5) WriteHp: guarded little-endian write (pinned buffer stands in for the band) ----

    [Fact]
    public void WriteHp_writes_the_u16_little_endian()
    {
        using var band = PinnedBuf.Of(256);
        LifeSap.WriteHp(Live, band.Addr, 0x1234);
        Assert.Equal(0x34, band.Bytes[Offsets.AHp]);
        Assert.Equal(0x12, band.Bytes[Offsets.AHp + 1]);
    }

    // ---- LW-145 fix 2: torn-write regression -- one W16 call, never two W8 halves ----

    [Fact]
    public void WriteHp_crossing_the_255_256_boundary_lands_as_one_16bit_write_never_two_torn_halves()
    {
        // A heal crossing 255 -> 256+ HP used to be two separate W8 calls (low byte, then high
        // byte): a torn-value window where the game's own threads could observe an intermediate
        // garbage HP (a mid-write read of the low byte alone). One W16 call closes the window.
        var mem = new FakeSparseMemory();
        long entryAddr = 0x5000;
        mem.MarkWritable(entryAddr + Offsets.AHp, 2);   // production gates Writable n=2 (BandHeal.cs, via the LifeSap forward)

        LifeSap.WriteHp(mem, entryAddr, 260);   // 0x104: crosses the 255/256 byte boundary

        Assert.Equal(260, mem.WrittenU16[entryAddr + Offsets.AHp]);
        Assert.Contains(entryAddr + Offsets.AHp, mem.WriteOrder);
        Assert.False(mem.Written.ContainsKey(entryAddr + Offsets.AHp));       // no low-byte W8
        Assert.False(mem.Written.ContainsKey(entryAddr + Offsets.AHp + 1));   // no high-byte W8
    }

    // ---- Main-hand-only activation contract (B2) ----
    // A Living Weapon earns kills in any hand, but commands its gift only from the main hand.
    // LifeSap resolves wielder via Wielder.TryResolveMainHand (RRHand-only match).

    [Fact]
    public void ActivatesOnMainHandOnly_is_documented_in_policy()
        => Assert.True(LifeSap.ActivatesOnMainHandOnly);

    // ---- LW-149 stage C: activation-edge log STRING PIN (the `_slog.Info` shape) ----
    // Written BEFORE LifeSap.Tick converts to ActivationEdge.Step, so it passes against the OLD
    // hand-rolled `if (active != _wasActive) { _wasActive = active; _slog.Info(...); }` idiom
    // first, then keeps passing unchanged once the guard is replaced -- proof the extraction never
    // touched the log call, its text, or its once-per-transition cadence.
    [Fact]
    public void Tick_activation_edge_logs_the_armed_line_exactly_once_then_stays_silent()
    {
        const int UmbralId = 56;   // LifeSap's own const is private; mirrored here for the fixture
        var mem = new FakeSparseMemory();
        var meta = new Dictionary<int, WeaponMeta>
        {
            [UmbralId] = new WeaponMeta
            {
                Name = "Umbral Rod", Wp = 1, Cat = "Rod", Formula = 1,
                Flavor = "A rod that drinks its foes' vigor", Signature = SapSig()
            }
        };
        var kills = new Dictionary<int, int> { [UmbralId] = Tuning.ProdThresholds[2] };   // tier 3

        // Roster slot 0: Umbral Rod in main hand.
        long rb = Offsets.RosterBase;
        mem.U8s[rb + Offsets.RLevel] = 30;
        mem.U8s[rb + Offsets.RBrave] = 60;
        mem.U8s[rb + Offsets.RFaith] = 55;
        mem.U16s[rb + Offsets.RRHand] = (ushort)UmbralId;

        // A deployed live band entry so ScopedLogger's armed gate (Wielder.AnyDeployedMainHand)
        // reports true -- the console/file line renders at Info, not demoted to Debug.
        long entry = Band.Entry(30);
        BandFixtures.SeedBandEntryCore(mem, entry, hp: 200, maxHp: 200, lvl: 30, br: 60, fa: 55, gx: 5, gy: 5);
        mem.U16s[entry + (Offsets.CWeapon - Offsets.BandEntry)] = (ushort)UmbralId;

        var sap = new LifeSap(meta, kills, mem: mem);

        var console = new List<string>();
        var file = new List<string>();
        ModLogger.Instance = new FileConsoleLogger(console.Add, file.Add) { LogLevel = LogLevel.Info };
        try
        {
            sap.Tick();   // rising edge: armed line fires once
            sap.Tick();   // steady state: must add nothing more
        }
        finally { ModLogger.UseNullLogger(); }

        const string onLine = "Umbral Rod at tier three is wielded on the field; its kills restore the wielder's HP.";
        Assert.Single(file, l => l.Contains(onLine) && l.Contains("[INFO]"));
        Assert.Single(console, l => l.Contains(onLine));
    }
}
