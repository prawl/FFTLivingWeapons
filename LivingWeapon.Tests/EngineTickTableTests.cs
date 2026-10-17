using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-186: the tick-phase TABLE itself as data -- Engine.BuildPhases(null) hands over the real
/// production rows with no Engine constructed and no memory touched (each row's Run closure
/// dereferences the engine lazily, so shape-only inspection is safe by contract as long as Run
/// is never invoked here; see BuildPhases's doc). Distinct from TickPhaseTests (the cadence
/// primitive's behavior) and from EngineTests (the instance-table pin and the regime drive).
/// The engine itself never reads After[] at runtime; these checks are what give the field its
/// meaning. They subsume EngineTests' deleted instance-table After walk (both tables come from
/// the same method, and the instance's After values are pinned in Phases_match), splitting its
/// one assertion into ordering and resolution so a dangling name cannot pass vacuously, and
/// add the non-battle gate allowlist.
/// </summary>
public class EngineTickTableTests
{
    [Fact]
    public void After_names_appear_at_a_strictly_lower_index()
    {
        var rows = Engine.BuildPhases(null);

        for (int i = 0; i < rows.Length; i++)
        {
            foreach (var cited in rows[i].After)
            {
                int citedIndex = Array.FindIndex(rows, r => r.Name == cited);
                // An unresolved name yields -1 and PASSES here by design: this test owns the
                // ordering half only. After_names_resolve_to_a_real_row below owns resolution,
                // so a row rename fails there instead of silently voiding this constraint.
                Assert.True(citedIndex < i,
                    $"'{rows[i].Name}' (index {i}) cites '{cited}' in After, but that row sits at index {citedIndex}, not strictly earlier.");
            }
        }
    }

    [Fact]
    public void After_names_resolve_to_a_real_row()
    {
        var rows = Engine.BuildPhases(null);

        // Names are the join key of every check in this file, the allowlist included: two rows
        // sharing a name would fold into one set entry in the gate test below and resolve
        // ambiguously here, so resolution is only sound while names are unique (LW-186 verify
        // round's catch).
        Assert.Equal(rows.Length, rows.Select(r => r.Name).Distinct().Count());

        var names = new HashSet<string>(rows.Select(r => r.Name));

        foreach (var row in rows)
            foreach (var cited in row.After)
                Assert.True(names.Contains(cited),
                    $"'{row.Name}' cites '{cited}' in After, but no row carries that name; the ordering test above passes vacuously on an unresolved name (-1 is always a lower index), so this is the assertion that catches a rename.");
    }

    [Fact]
    public void Rows_outside_the_non_battle_allowlist_gate_on_one_of_the_four_battle_gates()
    {
        // Run is an opaque closure, so "reaches a battle-memory write path" cannot be read off
        // the row itself. The reviewable artifact is therefore this allowlist: every row ALLOWED
        // to run outside battle, each with the reason it is safe there. Everything else must
        // carry TickGates.InBattle or one of the three per part battle gates by reference.
        var nonBattle = new HashSet<string>
        {
            "kit-barrage",      // KitLane gate: learn screens/menus read the JobCommand table outside battle; roster open AND the kit-lane guard armed is its write gate
            "kit-shadowblade",  // KitLane gate: same lane as kit-barrage
            "kit-provoke",      // KitLane gate: same lane as kit-barrage
            "gunslinger",       // TwinPistols gate (LW-393): writes roster equipment, not battle memory, and only while the roster door is open; snapshot/restore self-gates on s.NowIn
            "display-out",      // OutOfBattle gate: paints the equip card into menu buffers, not battle memory
            "icon-glow",        // OutOfBattle gate: LW-295 cycle B, plain file I/O against modded.pac, no memory touched at all
            "pool-locate",      // Always gate: reads heap memory only (finding pool regions); the paint that consumes the result stays InBattle/OutOfBattle-gated separately
            "extended-caps",    // Always gate: LW-346, two copy-protected code bytes widened once their pages read vanilla (a loaded save); never battle memory; no-op until the boot arm armed
            "extended-bag",     // Always gate: LW-348, reads the bag count bytes of the extended ids and writes the sidecar FILE on change; the bag changes in shops and in battle alike
            "extended-shops",   // Always gate: LW-354, a 512-byte compare of the game's shop table against our mirror page and a copy on change; never battle memory
        };

        var rows = Engine.BuildPhases(null);
        var battleGates = new[] { TickGates.InBattle, TickGates.KillCounting, TickGates.StatGrowth, TickGates.BandSignatures };
        var unGated = rows.Where(r => !battleGates.Any(g => ReferenceEquals(r.Gate, g)))
                          .Select(r => r.Name).ToHashSet();

        // Set equality, both directions: a battle row escaping its gate fails the first assert;
        // an allowlist entry going stale (row renamed, or the row now InBattle-gated) fails the
        // second, so the allowlist cannot quietly outlive the table it describes.
        var escaped = unGated.Except(nonBattle).ToList();
        Assert.True(escaped.Count == 0,
            $"rows not on the non-battle allowlist and not gated on a battle gate: {string.Join(", ", escaped)}");
        var stale = nonBattle.Except(unGated).ToList();
        Assert.True(stale.Count == 0,
            $"stale allowlist entries (no matching row, or the row now gates on a battle gate): {string.Join(", ", stale)}");
    }

    /// <summary>LW-261 test 9: the resumable pool-locate scan's own tick lane exists, gated
    /// Always (every tick, in and out of battle) rather than InBattle -- its progress must not
    /// depend on battle state any more than it depends on Display.Tick (see PoolLocator.cs's own
    /// class doc). Mirrors LW-186's own BuildPhases shape-test idiom (Assert.Same by reference,
    /// not by re-deriving gate behavior).</summary>
    [Fact]
    public void Pool_locate_phase_exists_with_the_Always_gate_and_runs_every_tick()
    {
        var rows = Engine.BuildPhases(null);
        var row = Array.Find(rows, r => r.Name == "pool-locate");

        Assert.NotNull(row);
        Assert.Same(TickGates.Always, row!.Gate);
        Assert.Equal(1, row.EveryNTicks);
    }

    // ---- LW-393: the gates now carry the roster door and the per part predicate ----

    private static TickPhaseState State(bool nowIn, bool rosterOpen, bool kitArmed = false, Func<Part, bool>? partOn = null)
        => new() { NowIn = nowIn, RosterOpen = rosterOpen, KitLaneArmed = kitArmed, PartOn = partOn ?? (_ => false) };

    [Fact]
    public void Battle_gates_need_the_roster_door_open()
    {
        Assert.True(TickGates.InBattle(State(nowIn: true, rosterOpen: true)));
        Assert.False(TickGates.InBattle(State(nowIn: true, rosterOpen: false)));
        Assert.True(TickGates.OutOfBattle(State(nowIn: false, rosterOpen: true)));
        Assert.False(TickGates.OutOfBattle(State(nowIn: false, rosterOpen: false)));
        Assert.False(TickGates.InBattle(State(nowIn: false, rosterOpen: true)));
        Assert.False(TickGates.OutOfBattle(State(nowIn: true, rosterOpen: true)));
    }

    [Fact]
    public void The_kit_lane_gate_needs_the_roster_door_and_the_lane()
    {
        Assert.True(TickGates.KitLane(State(false, rosterOpen: true, kitArmed: true)));
        Assert.False(TickGates.KitLane(State(false, rosterOpen: false, kitArmed: true)));
        Assert.False(TickGates.KitLane(State(false, rosterOpen: true, kitArmed: false)));
    }

    [Fact]
    public void The_always_gate_ignores_every_door_and_the_twin_pistols_gate_asks_for_its_part()
    {
        Assert.True(TickGates.Always(State(false, rosterOpen: false)));
        Assert.True(TickGates.TwinPistols(State(false, false, partOn: p => p == Part.TwinPistols)));
        Assert.False(TickGates.TwinPistols(State(false, true, partOn: p => p != Part.TwinPistols)));
    }

    [Fact]
    public void Every_row_declares_the_part_it_belongs_to_and_the_part_matches_its_name_family()
    {
        var rows = Engine.BuildPhases(null);
        foreach (var row in rows.Where(r => r.Name.StartsWith("kit-")))
            Assert.Equal(Part.WeaponCommands, row.Part);
        foreach (var row in rows.Where(r => r.Name.StartsWith("extended-")))
            Assert.Equal(Part.ExtendedInventory, row.Part);
        Assert.Equal(Part.TwinPistols, rows.Single(r => r.Name == "gunslinger").Part);
        Assert.Same(TickGates.TwinPistols, rows.Single(r => r.Name == "gunslinger").Gate);
    }

    // ---- LW-398: the three per part battle gates ----

    [Theory]
    [InlineData("KillCounting")]
    [InlineData("StatGrowth")]
    [InlineData("BandSignatures")]
    public void The_per_part_battle_gates_need_a_battle_and_their_part(string partName)
    {
        var part = Enum.Parse<Part>(partName);
        var gate = part switch
        {
            Part.KillCounting => TickGates.KillCounting,
            Part.StatGrowth => TickGates.StatGrowth,
            _ => TickGates.BandSignatures,
        };

        Assert.True(gate(State(nowIn: true, rosterOpen: true, partOn: p => p == part)));
        Assert.False(gate(State(nowIn: false, rosterOpen: true, partOn: _ => true)));
        Assert.False(gate(State(nowIn: true, rosterOpen: true, partOn: p => p != part)));
    }
}
