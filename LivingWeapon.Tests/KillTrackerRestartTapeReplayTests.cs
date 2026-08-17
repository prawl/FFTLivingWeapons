using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-233 residual (2026-08-17, owner-witnessed live drill): the deployed RestartSentinel fix
/// (147119f) FAILED live -- the tally went 21 -> 22 on a re-kill after a retry, and ZERO
/// "restart" flight records were written. Root cause, confirmed by reading the code (see
/// RestartSentinel.Policy.cs's ShouldOpenLatch/ShouldStash doc for the fix): KillTracker.Poll
/// passes BattleState.OnField (battleMode in {2,3,4}) as RestartSentinel.Tick's inLiveish input,
/// but the death -> game-over screen sits at battleMode 0 (NOT on-field) for ~8.75 SECONDS on the
/// banked live tape -- long enough for RestartSentinelPolicy.OutOfLiveRearmTicks (60 ticks, ~2s)
/// to fire the out-of-live re-arm repeatedly, zeroing _battleAgeTicks each time. By the time the
/// mod returns on-field and the credited corpse's revive is presented, battle age is only ~25-30
/// ticks -- nowhere near RestartSentinelPolicy.GraceTicks (150) -- so ShouldOpenLatch refuses
/// silently every time, and the retry's kills pay out a second time on the re-kill.
///
/// THIS FILE replays the banked tape (tools/probes/tapes/lw233_death_retry_live_20260817.jsonl)
/// tick-by-tick, deriving onField from the REAL BattleState.OnField instead of hand-authoring it.
/// RestartSentinelTests.cs's existing tests are vacuous exactly here -- they synthesize inLiveish
/// directly (Advance(s, n, inLiveish: true/false)) and never replay a genuine mode stream through
/// OnField, so none of them ever exercised the "battleMode 0 for seconds" shape that actually
/// broke the sentinel live. This file is the non-vacuous replacement: every input (battleMode, raw
/// actor-pointer null state) comes from the tape's own recorded edges, and onField is DERIVED by
/// calling the production BattleState.OnField, never hand-set.
/// </summary>
public class KillTrackerRestartTapeReplayTests
{
    private const string TapeFile = "lw233_death_retry_live_20260817.jsonl";
    private const int ReviveSlot = 15;
    private const int ReviveWeapon = 73;

    // The two identity tuples fed to PresentRevive -- SameId/SameId models the real tape (the
    // revived unit IS the one credited); CreditedId/OtherPresentedId models a mismatch (a
    // different encounter's unit landing in the same slot, the LW-108 shape the grace floor
    // alone must still catch). The tape carries no lvl/br/fa for slot 15 (only nameId, job, and
    // maxHp -- see ParseTapeNameId below), so this tuple stays a synthetic stand-in; only the
    // nameId half is drawn from the tape (punch list item 2, 2026-08-17).
    private static readonly (byte lvl, byte br, byte fa, ushort mhp) SameId = (10, 50, 50, 400);
    private static readonly (byte lvl, byte br, byte fa, ushort mhp) OtherId = (99, 89, 76, 352);
    // FINDING 0 (2026-08-17 verifier correction): PresentRevive now also requires a matching
    // nonzero nameId (TapeNameId/TapeNameId pairs with SameId/SameId the same way OtherNameId
    // pairs with OtherId below). TapeNameId is PARSED from the tape's own `victim` records
    // (`slot=15 alive=(nameId=378,job=85,...)`) rather than hand-fed, so this test's "matching
    // identity" input is the identity a real retry actually produced, not a hand-picked stand-in.
    private static readonly ushort TapeNameId = ParseTapeNameId(ReviveSlot);
    private const ushort OtherNameId = 777;   // any value that differs from TapeNameId -- a synthetic mismatch, not a claim about a real retry

    /// <summary>Parses the nameId this tape's own `victim` records recorded for
    /// <paramref name="slot"/> (`slot=15 alive=(nameId=378,job=85,undead=0,has=1) ...`) -- the
    /// SAME record kind docs/LIVE_LEDGER.md row [retry-preserves-credited-identity] cites as
    /// evidence that this tape's pre-retry credit and post-retry re-kill agree on nameId 378. Both
    /// occurrences in the tape carry the same value (the pre-retry credit and the post-retry
    /// re-kill), so the first match is authoritative.</summary>
    private static ushort ParseTapeNameId(int slot)
    {
        var re = new Regex($@"^slot={slot} alive=\(nameId=(\d+)");
        foreach (var r in LoadTape())
        {
            if (r.E != "victim") continue;
            var m = re.Match(r.D);
            if (m.Success) return ushort.Parse(m.Groups[1].Value);
        }
        throw new InvalidOperationException($"tape carries no victim record for slot {slot} -- fixture is stale or misnamed");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docs", "TODO.md")) &&
                Directory.Exists(Path.Combine(dir.FullName, "LivingWeapon")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("repo root (docs/TODO.md + LivingWeapon/) not found above the test bin dir");
    }

    private readonly record struct TapeRecord(long T, string E, string D);

    private static List<TapeRecord> LoadTape()
    {
        string path = Path.Combine(RepoRoot(), "tools", "probes", "tapes", TapeFile);
        var records = new List<TapeRecord>();
        foreach (string line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("hdr", out _)) continue;   // the header line, not a tick record
            records.Add(new TapeRecord(
                root.GetProperty("t").GetInt64(),
                root.GetProperty("e").GetString()!,
                root.GetProperty("d").GetString()!));
        }
        return records;
    }

    private static readonly Regex ModeRe = new(@"^battleMode \d+ -> (\d+)$");
    private static readonly Regex ActorRe = new(@"^pointer transition \S+ -> (\S+) ");

    /// <summary>Builds the regex naming the revive event for a specific slot+weapon so every
    /// replay in this file locates the SAME tape event.</summary>
    private static Regex ReviveRe(int slot, int weapon) =>
        new($@"battle slot {slot} at .* hit points 0 -> \d+ of \d+ \(weapons: {weapon}\)");

    /// <summary>
    /// Replays the tape from the death-screen's onset (the LAST "-> 0" battleMode transition
    /// before the named revive -- the start of the ~8.75s off-field stretch the live drill caught)
    /// through the revive itself, ticking the sentinel at the tape's own 33ms cadence and deriving
    /// onField via the real BattleState.OnField at every step. The sentinel is pre-rolled 200 ticks
    /// on-field first (mirrors every KillTrackerRestartTests.cs AdvanceTicks(t, 200) convention --
    /// this models a battle already deep past its OWN opening grace, not a fresh encounter) so the
    /// only thing under test is the mid-battle out-of-live re-arm, not a fresh-battle grace read.
    /// Presents the revive at the tape's own recorded moment with the given credited/presented
    /// identity tuples (see this class's SameId/OtherId doc) and <paramref name="feedNull"/>
    /// controlling whether the tape's real actor-pointer null is fed at all (false models a real
    /// Raise on the same timeline, with no retry evidence whatsoever). Returns the verdict plus the
    /// sentinel so callers can inspect LatchOpen afterward.
    /// </summary>
    private static (RevivePresentResult Verdict, RestartSentinel Sentinel) ReplayToRevive(
        int slot, int weapon,
        (byte lvl, byte br, byte fa, ushort mhp) creditedIdentity,
        (byte lvl, byte br, byte fa, ushort mhp) presentedIdentity,
        ushort creditedNameId, ushort presentedNameId,
        bool feedNull = true)
    {
        var tape = LoadTape();

        TapeRecord revive = tape.Find(r => r.E == "ev" && ReviveRe(slot, weapon).IsMatch(r.D));
        Assert.True(revive.T != 0, "the tape must contain the named slot/weapon revive event -- fixture is stale or misnamed");

        // The death-screen stretch: the LAST "-> 0" battleMode transition at/before the revive.
        long tStart = long.MinValue;
        foreach (var r in tape)
        {
            if (r.T > revive.T) break;
            if (r.E == "mode" && Regex.IsMatch(r.D, @"-> 0$")) tStart = r.T;
        }
        Assert.True(tStart > long.MinValue, "the tape must contain a battleMode -> 0 transition before the revive");

        var s = new RestartSentinel();
        for (int i = 0; i < 200; i++) s.Tick(rawActorNull: false, inLiveish: true);   // past the sentinel's own opening grace

        int battleMode = 0;    // the tStart record itself sets battleMode to 0
        bool rawNull = false;  // no null in flight yet at tStart
        int idx = 0;
        while (idx < tape.Count && tape[idx].T <= tStart) idx++;

        RevivePresentResult verdict = RevivePresentResult.Refuse;
        for (long t = tStart; ; t += 33)
        {
            while (idx < tape.Count && tape[idx].T <= t)
            {
                var r = tape[idx];
                if (r.E == "mode")
                {
                    var m = ModeRe.Match(r.D);
                    if (m.Success) battleMode = int.Parse(m.Groups[1].Value);
                }
                else if (r.E == "actor")
                {
                    var m = ActorRe.Match(r.D);
                    if (m.Success) rawNull = m.Groups[1].Value == "0x0";
                }
                idx++;
            }

            bool onField = BattleState.OnField(inBattle: true, battleMode);
            s.Tick(feedNull && rawNull, onField);

            if (t >= revive.T)
            {
                verdict = s.PresentRevive(slot, new List<int> { weapon }, viaFallback: false, healedFromZero: true,
                                           creditedIdentity, presentedIdentity, creditedNameId, presentedNameId);
                break;
            }
        }

        return (verdict, s);
    }

    /// <summary>
    /// THE LOAD-BEARING TEST. Slot 15's revive (weapon 73, "healing 306 ... hit points 0 -> 306")
    /// at tape t=2240078 is the exact moment the live drill's second payout happened: the corpse
    /// this tracker had credited came back alive during a checkpoint retry, but the sentinel's
    /// grace gate refused to reverse the credit because the out-of-live re-arm (repeatedly firing
    /// across the 8.75s battleMode-0 game-over screen) had zeroed battle age down to ~25-30 ticks
    /// by the time the revive landed. MUST fail against the pre-fix code (Refuse, grace still
    /// "holds" per the stale battle-age reading) and pass once the identity-gated grace exemption
    /// (RestartSentinelPolicy.ShouldOpenLatch/ShouldStash's `battleAgeTicks > GraceTicks ||
    /// identityMatches`) is wired in. This replay feeds SameId (a synthetic tuple -- the tape
    /// carries no lvl/br/fa for slot 15) but TapeNameId, PARSED from the tape's own `victim`
    /// records, as both the credited and presented nameId (punch list item 2, 2026-08-17): the
    /// tape's pre-retry credit and post-retry re-kill both read nameId 378 for this slot, so this
    /// is no longer a hand-picked stand-in, it is the identity a real retry actually produced.
    /// What remains UNPROVEN is only whether that holds in GENERAL for enemies
    /// (docs/LIVE_LEDGER.md row [retry-preserves-credited-identity], n=1) -- a DIFFERENT
    /// encounter's unit landing in the same band slot after LW-108's starved-bracket hole is not
    /// GUARANTEED to carry a different identity, merely EXPECTED to (see the companion test right
    /// below, which pins the code's behavior on a genuine mismatch, not a claim about what every
    /// real mismatch looks like).
    /// </summary>
    [Fact]
    public void Death_screen_tape_opens_the_latch_when_the_revived_identity_matches_the_credited_one()
    {
        var (verdict, sentinel) = ReplayToRevive(ReviveSlot, ReviveWeapon, SameId, SameId, TapeNameId, TapeNameId);

        // UncreditNow (not Stashed) also PROVES the second suspected problem -- the qualified-null
        // evidence surviving the out-of-live re-arm -- was never actually broken on this tape: the
        // null (actor pointer -> 0x0 at t=2239937) qualifies (NullPersistTicks=2, ~66ms) well
        // before the revive (t=2240078, 141ms later) and no re-arm fires between qualification and
        // presentation (the off-field stretch's last re-arm lands ~700ms before the null even
        // starts; the mod returns on-field partway through the null's own hold, which stops the
        // out-of-live counter for good). A Stashed result here would mean the null hadn't
        // qualified yet at presentation time -- it did, so the direct ShouldOpenLatch path fires,
        // and no production change to the null-streak/qualified-null bookkeeping was needed.
        Assert.Equal(RevivePresentResult.UncreditNow, verdict);
        Assert.True(sentinel.LatchOpen);
    }

    /// <summary>The LW-108 false-positive guard, still holding: the SAME tape replay, but the
    /// presented identity does not match what was credited (a different encounter's unit landing
    /// in the same band slot after the starved-bracket hole). Identity mismatch must NOT open the
    /// latch even though every other condition (qualified null, join window, credited+healed) is
    /// identical to the load-bearing test above -- grace alone is what protects this case, and it
    /// must still refuse since battle age never actually cleared 150 ticks on this tape.</summary>
    [Fact]
    public void Death_screen_tape_never_opens_the_latch_when_the_revived_identity_does_not_match()
    {
        var (verdict, sentinel) = ReplayToRevive(ReviveSlot, ReviveWeapon, OtherId, SameId, OtherNameId, TapeNameId);

        Assert.Equal(RevivePresentResult.Refuse, verdict);
        Assert.False(sentinel.LatchOpen);
    }

    /// <summary>A real Raise sanity check on the SAME tape shape: strip out the null entirely (as
    /// if the actor pointer never faulted) and confirm the latch never opens no matter how the
    /// identity/grace inputs land -- the null requirement is not something identity match can
    /// substitute for.</summary>
    [Fact]
    public void Death_screen_tape_without_any_null_never_opens_even_with_a_matching_identity()
    {
        var (verdict, sentinel) = ReplayToRevive(ReviveSlot, ReviveWeapon, SameId, SameId, TapeNameId, TapeNameId, feedNull: false);

        Assert.Equal(RevivePresentResult.Refuse, verdict);
        Assert.False(sentinel.LatchOpen);
    }
}
