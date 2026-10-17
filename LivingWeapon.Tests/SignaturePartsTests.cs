using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-393: the signature to part map and the per module gate inside the field signatures row.
/// </summary>
public class SignaturePartsTests
{
    private sealed class BandFake : ISignature
    {
        public int Ticks;
        public void Tick(in TickContext ctx) => Ticks++;
        public void ResetBattle() { }
    }

    private sealed class StaticFake : ISignature
    {
        public int Ticks;
        public void Tick(in TickContext ctx) => Ticks++;
        public void ResetBattle() { }
    }

    private sealed class Unmapped : ISignature
    {
        public void Tick(in TickContext ctx) { }
        public void ResetBattle() { }
    }

    [Fact]
    public void Closed_part_modules_are_never_ticked()
    {
        // The load bearing test. A TickOpen that ignores the predicate ticks both fakes and this goes red.
        var band = new BandFake();
        var stat = new StaticFake();
        var ctx = new TickContext(DateTime.Now, true, true, true);

        SignatureParts.TickOpen(new ISignature[] { band, stat },
            new[] { Part.BandSignatures, Part.Bulwark },
            p => p == Part.BandSignatures, in ctx);

        Assert.Equal(1, band.Ticks);
        Assert.Equal(0, stat.Ticks);
    }

    [Fact]
    public void Every_field_signature_type_maps_and_bulwark_is_the_static_one()
    {
        ModLogger.UseNullLogger();
        using var temp = TempDirs.Create("lw_sigparts_");
        var engine = new Engine(EngineTests.NestedModDir(temp), mem: EngineTests.HealthyMemory(), notice: (_, __) => { });

        Assert.Equal(engine.FieldTickOrder.Count, engine.FieldParts.Count);
        for (int i = 0; i < engine.FieldTickOrder.Count; i++)
        {
            var expected = engine.FieldTickOrder[i] == typeof(Bulwark) ? Part.Bulwark : Part.BandSignatures;
            Assert.Equal(expected, engine.FieldParts[i]);
        }
        Assert.Contains(Part.Bulwark, engine.FieldParts);
    }

    [Fact]
    public void An_unmapped_signature_type_throws_so_a_new_one_cannot_slip_in_unassigned()
    {
        Assert.Throws<InvalidOperationException>(() => SignatureParts.Of(new Unmapped()));
    }

    [Fact]
    public void The_kit_trio_maps_to_weapon_commands()
    {
        var mem = EngineTests.HealthyMemory();

        Assert.Equal(Part.WeaponCommands,
            SignatureParts.Of(new Barrage(new Dictionary<int, WeaponMeta>(), new Dictionary<int, int>(), mem)));
    }

    [Fact]
    public void Provoke_maps_to_its_own_part_not_weapon_commands()
    {
        Assert.Equal(Part.Provoke,
            SignatureParts.Of(new Provoke(new Dictionary<int, WeaponMeta>(), new Dictionary<int, int>(), new FakeSparseMemory())));
    }
}
