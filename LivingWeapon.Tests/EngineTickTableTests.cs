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
    public void Rows_outside_the_non_battle_allowlist_gate_on_InBattle()
    {
        // Run is an opaque closure, so "reaches a battle-memory write path" cannot be read off
        // the row itself. The reviewable artifact is therefore this allowlist: every row ALLOWED
        // to run outside battle, each with the reason it is safe there. Everything else must
        // carry TickGates.InBattle by reference.
        var nonBattle = new HashSet<string>
        {
            "kit-barrage",      // KitLane gate: learn screens/menus read the JobCommand table outside battle; the kit-lane guard (LW-112) is its write gate
            "kit-shadowblade",  // KitLane gate: same lane as kit-barrage
            "kit-provoke",      // KitLane gate: same lane as kit-barrage
            "gunslinger",       // Always gate: writes roster equipment, not battle memory; snapshot/restore self-gates on s.NowIn
            "display-out",      // OutOfBattle gate: paints the equip card into menu buffers, not battle memory
            "icon-glow",        // OutOfBattle gate: LW-295 cycle B, plain file I/O against modded.pac, no memory touched at all
            "pool-locate",      // Always gate: reads heap memory only (finding pool regions); the paint that consumes the result stays InBattle/OutOfBattle-gated separately
            "extended-caps",    // Always gate: LW-346, two copy-protected code bytes widened once their pages read vanilla (a loaded save); never battle memory; no-op until the boot arm armed
            "extended-bag",     // Always gate: LW-348, reads the bag count bytes of the extended ids and writes the sidecar FILE on change; the bag changes in shops and in battle alike
            "extended-shops",   // Always gate: LW-354, a 512-byte compare of the game's shop table against our mirror page and a copy on change; never battle memory
        };

        var rows = Engine.BuildPhases(null);
        var unGated = rows.Where(r => !ReferenceEquals(r.Gate, TickGates.InBattle))
                          .Select(r => r.Name).ToHashSet();

        // Set equality, both directions: a battle row escaping its gate fails the first assert;
        // an allowlist entry going stale (row renamed, or the row now InBattle-gated) fails the
        // second, so the allowlist cannot quietly outlive the table it describes.
        var escaped = unGated.Except(nonBattle).ToList();
        Assert.True(escaped.Count == 0,
            $"rows not on the non-battle allowlist and not gated on TickGates.InBattle: {string.Join(", ", escaped)}");
        var stale = nonBattle.Except(unGated).ToList();
        Assert.True(stale.Count == 0,
            $"stale allowlist entries (no matching row, or the row now gates on InBattle): {string.Join(", ", stale)}");
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
}
