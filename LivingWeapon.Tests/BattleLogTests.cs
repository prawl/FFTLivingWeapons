using System.Collections.Generic;
using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The dev battle-event timeline: per-slot HP/position diffs emitted as timestamped log lines so
/// an on-screen action and its capture can be compared at tick granularity ("the logs lag the
/// game" hardening). Verbose events are DEV-only; the sink is injected so tests read the lines.
/// </summary>
public class BattleLogTests
{
    private static (BattleLog log, List<string> lines) Make(bool verbose = true)
    {
        var lines = new List<string>();
        return (new BattleLog(verbose, lines.Add), lines);
    }

    [Fact]
    public void First_sighting_baselines_silently()
    {
        var (log, lines) = Make();
        log.Observe(slot: 3, hp: 120, maxHp: 200, gx: 5, gy: 5, actor: "10");
        Assert.Empty(lines);
    }

    [Fact]
    public void Damage_is_logged_with_the_delta_and_actor()
    {
        var (log, lines) = Make();
        log.Observe(3, 120, 200, 5, 5, "10");
        log.Observe(3, 63, 200, 5, 5, "10");
        string ev = Assert.Single(lines);
        Assert.Contains("damage 57", ev);
        Assert.Contains("120 -> 63 of 200", ev);
        Assert.Contains("(weapons: 10)", ev);
    }

    [Fact]
    public void Healing_is_logged()
    {
        var (log, lines) = Make();
        log.Observe(3, 63, 200, 5, 5, "");
        log.Observe(3, 100, 200, 5, 5, "");
        string ev = Assert.Single(lines);
        Assert.Contains("healing 37", ev);
        Assert.DoesNotContain("(weapons:", ev);   // no actor tag when nothing is latched
    }

    [Fact]
    public void A_move_is_logged_with_both_positions()
    {
        var (log, lines) = Make();
        log.Observe(7, 100, 100, 2, 9, "");
        log.Observe(7, 100, 100, 4, 8, "");
        string ev = Assert.Single(lines);
        Assert.Contains("from (2,9) to (4,8)", ev);
    }

    [Fact]
    public void Fatal_damage_logs_the_drop_to_zero()
    {
        var (log, lines) = Make();
        log.Observe(3, 57, 200, 5, 5, "10");
        log.Observe(3, 0, 200, 5, 5, "10");
        Assert.Contains("57 -> 0 of 200", Assert.Single(lines));
    }

    [Fact]
    public void Quiet_when_not_verbose()
    {
        var (log, lines) = Make(verbose: false);
        log.Observe(3, 120, 200, 5, 5, "10");
        log.Observe(3, 63, 200, 6, 6, "10");
        Assert.Empty(lines);
    }

    [Fact]
    public void ResetBattle_rebaselines_every_slot()
    {
        var (log, lines) = Make();
        log.Observe(3, 120, 200, 5, 5, "");
        log.ResetBattle();
        log.Observe(3, 63, 200, 6, 6, "");   // first sighting of the new battle: silent baseline
        Assert.Empty(lines);
        log.Observe(3, 50, 200, 6, 6, "");
        Assert.Single(lines.Where(l => l.Contains("damage 13")));
    }
}
