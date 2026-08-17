using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-233 punch list item 5 (2026-08-17): pins the EXACT flight-record and console strings the
/// owner's live retry drill reads to tell "fired correctly" from "silently missed" -- the
/// latch-open record (including its grace-exemption phrasing), every no-open refusal's reason=
/// value, the stash and stash-drop records (punch list item 3), and the shared IdentityTap
/// identity formatting every one of them rides on. A future refactor that quietly renames any of
/// these strings now breaks a test HERE, not the owner's drill. RestartSentinelTests.cs pins
/// behavior; this file pins TEXT only, so the two suites can drift independently without either
/// one growing vague.
/// </summary>
public class RestartSentinelRecordTextTests
{
    private const int Grace = RestartSentinelPolicy.GraceTicks;
    private const int Persist = RestartSentinelPolicy.NullPersistTicks;
    private const int Window = RestartSentinelPolicy.JoinWindowTicks;

    private static readonly (byte lvl, byte br, byte fa, ushort mhp) SameId = (10, 50, 50, 400);
    private static readonly (byte lvl, byte br, byte fa, ushort mhp) OtherId = (99, 89, 76, 352);
    private const ushort SameNameId = 555;
    private const ushort OtherNameId = 777;

    private static List<int> W(int id) => new() { id };

    private static (RestartSentinel Sentinel, List<(string Type, string Payload)> Recorded) NewSentinel()
    {
        var recorded = new List<(string, string)>();
        return (new RestartSentinel((t, p) => recorded.Add((t, p))), recorded);
    }

    private static void Advance(RestartSentinel s, int n, bool rawNull = false, bool inLiveish = true)
    {
        for (int i = 0; i < n; i++) s.Tick(rawNull, inLiveish);
    }

    private static List<(string Type, string Payload)> RestartRecords(List<(string Type, string Payload)> recorded) =>
        recorded.FindAll(r => r.Type == "restart");

    // ---- IdentityTap: the shared credited/presented formatter every record below rides on ----

    [Fact]
    public void IdentityTap_formats_L_B_F_H_N_for_both_the_credited_and_presented_halves()
    {
        var (s, recorded) = NewSentinel();

        // Distinct digits in every field on both sides so a field-transposition bug (e.g. lvl and
        // maxHp swapped) cannot hide behind a coincidental match.
        s.PresentRevive(9, W(1), false, healedFromZero: true,
            (11, 22, 33, 444), (66, 77, 88, 999), 5555, 6666);

        string payload = Assert.Single(RestartRecords(recorded)).Payload;
        Assert.Contains("credited=(L11B22F33H444N5555) presented=(L66B77F88H999N6666)", payload);
    }

    // ---- no-open reason= values ----

    [Fact]
    public void No_open_reason_not_credited_is_exact_and_carries_no_identity_tap()
    {
        var (s, recorded) = NewSentinel();

        var verdict = s.PresentRevive(3, new List<int>(), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId);

        Assert.Equal(RevivePresentResult.Refuse, verdict);
        Assert.Equal("no-open reason=not-credited slot=3", Assert.Single(RestartRecords(recorded)).Payload);
    }

    [Fact]
    public void No_open_reason_not_healed_from_zero_is_exact_and_carries_no_identity_tap()
    {
        var (s, recorded) = NewSentinel();

        var verdict = s.PresentRevive(3, W(1), false, healedFromZero: false, SameId, SameId, SameNameId, SameNameId);

        Assert.Equal(RevivePresentResult.Refuse, verdict);
        Assert.Equal("no-open reason=not-healed-from-zero slot=3", Assert.Single(RestartRecords(recorded)).Payload);
    }

    [Fact]
    public void No_open_reason_no_qualified_null_is_exact()
    {
        var (s, recorded) = NewSentinel();
        Advance(s, Grace + 1);   // past grace, but no null has ever been observed

        var verdict = s.PresentRevive(4, W(1), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId);

        Assert.Equal(RevivePresentResult.Refuse, verdict);
        string expected = "no-open reason=no-qualified-null slot=4, " +
                           "credited=(L10B50F50H400N555) presented=(L10B50F50H400N555)";
        Assert.Equal(expected, Assert.Single(RestartRecords(recorded)).Payload);
    }

    [Fact]
    public void No_open_reason_outside_join_window_is_exact_and_takes_priority_over_a_mismatched_identity()
    {
        var (s, recorded) = NewSentinel();
        Advance(s, Grace + 1);
        Advance(s, Persist, rawNull: true);   // qualifies; ticksSinceQualifiedNull resets to 0 here
        Advance(s, Window + 1, rawNull: false);   // Window+1 more ticks pass -> ticksSinceQualifiedNull == Window+1, still on-field

        // Deliberately a mismatched identity too -- proves outside-join-window is reported ahead of
        // grace-not-cleared (PresentRevive's own "ties break in this priority order" ordering).
        var verdict = s.PresentRevive(5, W(1), false, healedFromZero: true, OtherId, SameId, OtherNameId, SameNameId);

        Assert.Equal(RevivePresentResult.Refuse, verdict);
        string expected = "no-open reason=outside-join-window slot=5, " +
                           "credited=(L99B89F76H352N777) presented=(L10B50F50H400N555)";
        Assert.Equal(expected, Assert.Single(RestartRecords(recorded)).Payload);
    }

    [Fact]
    public void No_open_reason_grace_not_cleared_is_exact()
    {
        var (s, recorded) = NewSentinel();
        Advance(s, Grace - Persist);
        Advance(s, Persist, rawNull: true);   // battle age lands EXACTLY at Grace, not past it

        var verdict = s.PresentRevive(6, W(1), false, healedFromZero: true, OtherId, SameId, OtherNameId, SameNameId);

        Assert.Equal(RevivePresentResult.Refuse, verdict);
        string expected = "no-open reason=grace-not-cleared slot=6, " +
                           "credited=(L99B89F76H352N777) presented=(L10B50F50H400N555)";
        Assert.Equal(expected, Assert.Single(RestartRecords(recorded)).Payload);
    }

    // ---- latch-open record, plain and grace-exempted ----

    [Fact]
    public void Latch_open_record_is_exact_past_grace_with_no_grace_exemption_phrase()
    {
        var (s, recorded) = NewSentinel();
        Advance(s, Grace + 1);
        Advance(s, Persist, rawNull: true);   // qualifies this same tick -> 0 ticks lead

        s.PresentRevive(0, W(1), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId);

        string expected = $"latch open (null qualified 0 ticks before this revive, battle age {Grace + 1 + Persist} ticks, " +
                           "credited=(L10B50F50H400N555) presented=(L10B50F50H400N555))";
        Assert.Equal(expected, Assert.Single(RestartRecords(recorded)).Payload);
    }

    [Fact]
    public void Latch_open_record_carries_the_grace_exemption_phrase_when_identity_alone_clears_it()
    {
        var (s, recorded) = NewSentinel();
        Advance(s, Grace - Persist);
        Advance(s, Persist, rawNull: true);   // battle age lands EXACTLY at Grace, not past it

        s.PresentRevive(0, W(1), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId);

        string expected = $"latch open (null qualified 0 ticks before this revive, battle age {Grace} ticks, " +
                           "grace exempted by a matching revived identity, " +
                           "credited=(L10B50F50H400N555) presented=(L10B50F50H400N555))";
        Assert.Equal(expected, Assert.Single(RestartRecords(recorded)).Payload);
    }

    // ---- stash / stash-drop (punch list item 3) ----

    [Fact]
    public void Stash_record_is_exact_when_a_0ms_join_defers_the_verdict()
    {
        var (s, recorded) = NewSentinel();
        Advance(s, 5);                  // nowhere near Grace -- identity alone must clear the stash gate
        Advance(s, 1, rawNull: true);   // streak == 1, not yet qualified

        var verdict = s.PresentRevive(7, W(42), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId);

        Assert.Equal(RevivePresentResult.Stashed, verdict);
        string expected = "stash slot=7 credited=(L10B50F50H400N555) presented=(L10B50F50H400N555)";
        Assert.Equal(expected, Assert.Single(RestartRecords(recorded)).Payload);
    }

    [Fact]
    public void Stash_drop_record_is_exact_when_the_null_breaks_before_qualifying()
    {
        var (s, recorded) = NewSentinel();
        Advance(s, 5);
        Advance(s, 1, rawNull: true);
        var verdict = s.PresentRevive(7, W(42), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId);
        Assert.Equal(RevivePresentResult.Stashed, verdict);
        recorded.Clear();   // isolate the drop record from the stash record above

        // The stash entry's TTL is NullPersistTicks (2): one non-qualifying tick decrements it to
        // 1 (still held), the next drops it.
        Advance(s, 1, rawNull: false);
        Assert.Empty(RestartRecords(recorded));   // not dropped yet -- one tick of TTL still remains
        Advance(s, 1, rawNull: false);

        string expected = "stash-drop slot=7 credited=(L10B50F50H400N555) presented=(L10B50F50H400N555)";
        Assert.Equal(expected, Assert.Single(RestartRecords(recorded)).Payload);
    }
}
