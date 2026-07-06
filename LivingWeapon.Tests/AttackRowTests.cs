using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// AttackRow.cs is LW-31 stage 3's runtime half (docs/TODO.md): it owns EVERY read/write of the
/// live 36-byte Attack-command record (AttackRow.Policy.cs's class doc has the full geometry).
/// Stateless per call, shape-based (Classify decides, Paint/Restore only ever act on a shape they
/// are willing to touch), no per-hit memory of its own, AttackCard.cs holds the cache/anchor
/// state and calls these methods each tick. Exercised directly here (bypassing the census/repaint
/// pipeline) since these methods have no dependency on it.
/// </summary>
public class AttackRowTests
{
    private const long TableBase = 0x7000000000;

    private static byte[] Read(AttackCardMemory mem, long addr, int len)
    {
        Assert.True(mem.TryReadBytes(addr, len, out var buf));
        return buf;
    }

    private static (AttackCardMemory mem, long labelAddr, long descAddr, long recordAddr) Rig(string descText = null!, int? oursRowNameChars = null)
    {
        var mem = new AttackCardMemory();
        mem.AddAttackTable(TableBase, 1, descText ?? AttackCardText.VanillaDesc, oursRowNameChars);
        var (labelAddr, descAddr) = AttackCardTableFixture.Addrs(TableBase, 1);
        long recordAddr = AttackCardMemory.RecordAddrFor(TableBase, 1);
        return (mem, labelAddr, descAddr, recordAddr);
    }

    [Fact]
    public void Classify_reads_a_vanilla_shaped_record_as_Vanilla()
    {
        var (mem, labelAddr, _, _) = Rig();
        var row = new AttackRow(mem);
        Assert.Equal(AttackRowShape.Vanilla, row.Classify(labelAddr));
    }

    [Fact]
    public void Classify_reads_an_already_painted_record_as_Ours()
    {
        var (mem, labelAddr, _, _) = Rig(oursRowNameChars: 10);
        var row = new AttackRow(mem);
        Assert.Equal(AttackRowShape.Ours, row.Classify(labelAddr));
    }

    [Fact]
    public void Classify_reads_a_mismatched_record_as_Foreign()
    {
        var mem = new AttackCardMemory();
        mem.AddHeapRegion(TableBase, AttackCardTableFixture.Build(1, AttackCardText.VanillaDesc));
        long recordAddr = AttackCardMemory.RecordAddrFor(TableBase, 1);
        mem.AddHeapRegion(recordAddr, AttackCardTableFixture.BuildForeignRecord());
        var (labelAddr, _) = AttackCardTableFixture.Addrs(TableBase, 1);

        var row = new AttackRow(mem);
        Assert.Equal(AttackRowShape.Foreign, row.Classify(labelAddr));
    }

    [Fact]
    public void Classify_reads_a_missing_record_as_Unreadable()
    {
        var mem = new AttackCardMemory();
        mem.AddHeapRegion(TableBase, AttackCardTableFixture.Build(1, AttackCardText.VanillaDesc));
        // Deliberately no record region added at all.
        var (labelAddr, _) = AttackCardTableFixture.Addrs(TableBase, 1);

        var row = new AttackRow(mem);
        Assert.Equal(AttackRowShape.Unreadable, row.Classify(labelAddr));
    }

    [Fact]
    public void Paint_writes_the_image_then_the_offset_bytes_on_a_vanilla_record()
    {
        var (mem, labelAddr, descAddr, recordAddr) = Rig();
        var row = new AttackRow(mem);
        byte[] image = AttackRow.BuildImage("Windrunner", "Kills: 3.");

        bool wrote = row.Paint(labelAddr, image, rowNameChars: "Windrunner".Length);

        Assert.True(wrote);
        Assert.Equal(image, Read(mem, descAddr, image.Length));
        Assert.Equal(AttackRow.OffsetBytes("Windrunner".Length), Read(mem, recordAddr, 8));
        Assert.Equal(AttackRowShape.Ours, row.Classify(labelAddr));
    }

    [Fact]
    public void Paint_refuses_a_foreign_record()
    {
        var mem = new AttackCardMemory();
        mem.AddHeapRegion(TableBase, AttackCardTableFixture.Build(1, AttackCardText.VanillaDesc));
        long recordAddr = AttackCardMemory.RecordAddrFor(TableBase, 1);
        mem.AddHeapRegion(recordAddr, AttackCardTableFixture.BuildForeignRecord());
        var (labelAddr, descAddr) = AttackCardTableFixture.Addrs(TableBase, 1);
        byte[] descBefore = Read(mem, descAddr, AttackRow.FootprintBytes);

        var row = new AttackRow(mem);
        bool wrote = row.Paint(labelAddr, AttackRow.BuildImage("Windrunner", "Kills: 3."), rowNameChars: 10);

        Assert.False(wrote);
        Assert.Equal(descBefore, Read(mem, descAddr, AttackRow.FootprintBytes));
        Assert.Empty(mem.WrittenAddrs);
    }

    [Fact]
    public void Restore_writes_offsets_then_the_vanilla_image_on_an_Ours_record()
    {
        var (mem, labelAddr, descAddr, recordAddr) = Rig(descText: "Windrunner Kills: 3.", oursRowNameChars: 10);
        var row = new AttackRow(mem);

        bool wrote = row.Restore(labelAddr);

        Assert.True(wrote);
        byte[] expectedImage = new byte[AttackRow.FootprintBytes];
        System.Text.Encoding.ASCII.GetBytes(AttackCardText.VanillaDesc).CopyTo(expectedImage, 0);
        Assert.Equal(expectedImage, Read(mem, descAddr, AttackRow.FootprintBytes));
        Assert.Equal(AttackRow.VanillaOffsetBytes, Read(mem, recordAddr, 8));
        Assert.Equal(AttackRowShape.Vanilla, row.Classify(labelAddr));
    }

    [Fact]
    public void Restore_refuses_a_vanilla_shaped_record_the_callers_decision()
    {
        var (mem, labelAddr, descAddr, _) = Rig();
        byte[] descBefore = Read(mem, descAddr, AttackRow.FootprintBytes);
        var row = new AttackRow(mem);

        bool wrote = row.Restore(labelAddr);

        Assert.False(wrote);
        Assert.Equal(descBefore, Read(mem, descAddr, AttackRow.FootprintBytes));
        Assert.Empty(mem.WrittenAddrs);
    }

    [Fact]
    public void Restore_refuses_a_foreign_record()
    {
        var mem = new AttackCardMemory();
        mem.AddHeapRegion(TableBase, AttackCardTableFixture.Build(1, AttackCardText.VanillaDesc));
        long recordAddr = AttackCardMemory.RecordAddrFor(TableBase, 1);
        mem.AddHeapRegion(recordAddr, AttackCardTableFixture.BuildForeignRecord());
        var (labelAddr, _) = AttackCardTableFixture.Addrs(TableBase, 1);

        var row = new AttackRow(mem);
        Assert.False(row.Restore(labelAddr));
        Assert.Empty(mem.WrittenAddrs);
    }

    [Fact]
    public void Restore_refuses_an_unreadable_record()
    {
        var mem = new AttackCardMemory();
        mem.AddHeapRegion(TableBase, AttackCardTableFixture.Build(1, AttackCardText.VanillaDesc));
        var (labelAddr, _) = AttackCardTableFixture.Addrs(TableBase, 1);

        var row = new AttackRow(mem);
        Assert.False(row.Restore(labelAddr));
        Assert.Empty(mem.WrittenAddrs);
    }

    // ---- Write order (item i): image before offsets on paint; offsets before image on restore;
    // offsets are ONE 8-byte write. WrittenAddrs preserves call order (AttackCardMemory.WriteBytes
    // appends to it on every call), so the ORDER of addresses recorded proves the write order. ----

    [Fact]
    public void Paint_writes_the_footprint_address_before_the_record_address()
    {
        var (mem, labelAddr, descAddr, recordAddr) = Rig();
        var row = new AttackRow(mem);
        row.Paint(labelAddr, AttackRow.BuildImage("Windrunner", "Kills: 3."), rowNameChars: 10);

        Assert.Equal(2, mem.WrittenAddrs.Count);
        Assert.Equal(descAddr, mem.WrittenAddrs[0]);
        Assert.Equal(recordAddr, mem.WrittenAddrs[1]);
    }

    [Fact]
    public void Restore_writes_the_record_address_before_the_footprint_address()
    {
        var (mem, labelAddr, descAddr, recordAddr) = Rig(descText: "Windrunner Kills: 3.", oursRowNameChars: 10);
        var row = new AttackRow(mem);
        row.Restore(labelAddr);

        Assert.Equal(2, mem.WrittenAddrs.Count);
        Assert.Equal(recordAddr, mem.WrittenAddrs[0]);
        Assert.Equal(descAddr, mem.WrittenAddrs[1]);
    }

    [Fact]
    public void The_record_write_is_exactly_one_8_byte_call()
    {
        var (mem, labelAddr, _, recordAddr) = Rig();
        var row = new AttackRow(mem);
        row.Paint(labelAddr, AttackRow.BuildImage("Windrunner", "Kills: 3."), rowNameChars: 10);

        Assert.Single(mem.WrittenAddrs, a => a == recordAddr);
    }
}
