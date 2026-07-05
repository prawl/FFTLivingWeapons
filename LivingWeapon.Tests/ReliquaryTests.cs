using System.Collections.Generic;
using System.IO;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Reliquary is the deed-recording seam wired into KillTracker.CreditKill via IDeedSink
/// (KillTrackerDeedTests.cs owns that wiring). This file owns Reliquary's OWN behavior: the
/// Mark toast enqueue (stage 3, decision 11's disabled-toast inertness) and the flight taps.
/// Each test works in its own temp legends.json directory so parallel runs never collide.
/// </summary>
public class ReliquaryTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "lw_reliquary_" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    private static VictimSnapshot Victim(ushort nameId, byte job, bool undead = false)
        => new(true, nameId, job, undead);

    [Fact]
    public void Mark_toast_enqueues_with_key_1000_plus_archetype()
    {
        var store = LegendStore.Load(TempDir());
        var meta = new Dictionary<int, WeaponMeta> { [9] = new WeaponMeta { Name = "Kiyomori" } };
        var kills = new Dictionary<int, int>();
        var toast = new BannerToast(meta, kills, enabled: true);
        var reliquary = new Reliquary(store, toast, meta);

        int threshold = Tuning.MarkThresholds[0];
        for (int i = 0; i < threshold; i++)
            reliquary.RecordDeed(9, Victim((ushort)(100 + i), 77));   // job 77 (Archer) -> Human every time

        var markEvent = Assert.Single(toast._queue,
            q => q.weaponId == 9 && q.tier == 1000 + (int)VictimClass.Archetype.Human);
        Assert.Equal("Kiyomori has earned its Mark: Manslayer!", markEvent.payload);
    }

    [Fact]
    public void Disabled_toasts_stay_fully_inert()
    {
        // decision 11: Engine passes toast: null when BannerToasts is disabled -- Reliquary must
        // still record the deed (the ledger fact is unconditional) but never touch a toast queue.
        var store = LegendStore.Load(TempDir());
        var meta = new Dictionary<int, WeaponMeta> { [9] = new WeaponMeta { Name = "Kiyomori" } };
        var reliquary = new Reliquary(store, toast: null, meta);

        int threshold = Tuning.MarkThresholds[0];
        var ex = Record.Exception(() =>
        {
            for (int i = 0; i < threshold; i++)
                reliquary.RecordDeed(9, Victim((ushort)(100 + i), 77));
        });

        Assert.Null(ex);
        Assert.Contains((int)VictimClass.Archetype.Human, store.Get(9).Marks);   // the deed record is guaranteed regardless of toast
    }

    [Fact]
    public void Recorder_receives_mark_earned_and_deed_miss_taps()
    {
        var store = LegendStore.Load(TempDir());
        var meta = new Dictionary<int, WeaponMeta> { [9] = new WeaponMeta { Name = "Kiyomori" } };
        var recorded = new List<(string type, string payload)>();
        var reliquary = new Reliquary(store, toast: null, meta, recorder: (t, p) => recorded.Add((t, p)));

        int threshold = Tuning.MarkThresholds[0];
        for (int i = 0; i < threshold; i++)
            reliquary.RecordDeed(9, Victim((ushort)(100 + i), 77));
        reliquary.DeedMiss(3);

        Assert.Contains(recorded, r => r.type == "mark-earned" && r.payload.Contains("weapon=9"));
        Assert.Contains(recorded, r => r.type == "deed-miss" && r.payload.Contains("slot=3"));
    }

    // --- Logging facelift: the per-battle Marks ledger + the [mark] console line ---

    [Fact]
    public void Marks_earned_accumulate_in_the_battle_ledger_and_reset_clears_it()
    {
        var store = LegendStore.Load(TempDir());
        var meta = new Dictionary<int, WeaponMeta> { [9] = new WeaponMeta { Name = "Kiyomori" } };
        var reliquary = new Reliquary(store, toast: null, meta);

        int threshold = Tuning.MarkThresholds[0];
        for (int i = 0; i < threshold; i++)
            reliquary.RecordDeed(9, Victim((ushort)(100 + i), 77));   // Human every time

        var mark = Assert.Single(reliquary.BattleMarks);
        Assert.Equal(9, mark.weaponId);
        Assert.Equal(VictimClass.Archetype.Human, mark.mark);

        reliquary.ResetBattle();
        Assert.Empty(reliquary.BattleMarks);
    }

    [Fact]
    public void An_earned_Mark_logs_the_mark_console_line()
    {
        var store = LegendStore.Load(TempDir());
        var meta = new Dictionary<int, WeaponMeta> { [9] = new WeaponMeta { Name = "Kiyomori" } };
        var reliquary = new Reliquary(store, toast: null, meta);

        var console = new List<string>();
        var prior = ModLogger.Instance;
        ModLogger.Instance = new FileConsoleLogger(console.Add, _ => { });
        try
        {
            int threshold = Tuning.MarkThresholds[0];
            for (int i = 0; i < threshold; i++)
                reliquary.RecordDeed(9, Victim((ushort)(100 + i), 77));
            Assert.Contains(console, l => l.Contains("[INFO]") && l.Contains("Kiyomori earns the Mark of the Manslayer."));
        }
        finally { ModLogger.Instance = prior; }
    }
}
