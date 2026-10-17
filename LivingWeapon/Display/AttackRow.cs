namespace LivingWeapon;

/// <summary>
/// LW-31 stage 3 runtime half. Reads and writes the live 36-byte Attack-command record (geometry
/// in AttackRow.Policy.cs, proven 2026-07-06). Stateless per call; AttackCard.cs holds the
/// cache/anchor state and calls these once per synced hit.
/// </summary>
internal sealed partial class AttackRow
{
    private readonly IGameMemory _mem;

    public AttackRow(IGameMemory mem) => _mem = mem;

    /// <summary>Reads the 36-byte record at LabelAddr - RecordGap and classifies its shape. Never
    /// writes. Unreadable when the record cannot be read at all (foreign heap layout or a freed buffer).</summary>
    internal AttackRowShape Classify(long labelAddr)
    {
        long recordAddr = labelAddr - RecordGap;
        if (!_mem.TryReadBytes(recordAddr, RecordBytes, out var raw)) return AttackRowShape.Unreadable;

        var rec = AttackRecord.Parse(raw);
        if (IsVanillaShape(rec)) return AttackRowShape.Vanilla;
        if (IsOurShape(rec)) return AttackRowShape.Ours;
        return AttackRowShape.Foreign;
    }

    /// <summary>Writes a fresh split image. The caller has verified the label bytes and the
    /// 74-byte payload (AttackRow.Policy.BuildImage enforces the length). Writes the desc footprint
    /// first, then the {nameOff, descOff} pair as one 8-byte write, the reverse of
    /// <see cref="Restore"/>, so a mid-transition read never sees a record pointing at the wrong text.
    /// Only writes when <see cref="Classify"/> reads Vanilla or Ours; false otherwise or when a target is unwritable.</summary>
    internal bool Paint(long labelAddr, byte[] image, int rowNameChars)
    {
        var shape = Classify(labelAddr);
        if (shape != AttackRowShape.Vanilla && shape != AttackRowShape.Ours) return false;

        long descAddr = labelAddr + AttackCardProbeText.DescStart(0, 1);
        long recordAddr = labelAddr - RecordGap;
        if (!_mem.Writable(descAddr, image.Length)) return false;
        if (!_mem.Writable(recordAddr, 8)) return false;

        _mem.WriteBytes(descAddr, image);
        _mem.WriteBytes(recordAddr, OffsetBytes(rowNameChars));
        return true;
    }

    /// <summary>Restores vanilla, only when <see cref="Classify"/> reads Ours. Writes the vanilla
    /// offset pair first, then the 74-byte vanilla image (reverse of <see cref="Paint"/>), so a
    /// record still Ours-shaped never points at anything but vanilla text. A Vanilla record needs
    /// no write; any text-only restore is the caller's call (AttackCard.Paint.cs SyncHit).</summary>
    internal bool Restore(long labelAddr)
    {
        if (Classify(labelAddr) != AttackRowShape.Ours) return false;

        long descAddr = labelAddr + AttackCardProbeText.DescStart(0, 1);
        long recordAddr = labelAddr - RecordGap;
        if (!_mem.Writable(recordAddr, 8)) return false;
        if (!_mem.Writable(descAddr, FootprintBytes)) return false;

        _mem.WriteBytes(recordAddr, VanillaOffsetBytes);
        _mem.WriteBytes(descAddr, VanillaImageBytes);
        return true;
    }

    /// <summary>The flat vanilla desc, encoded once as the 74-byte image Restore writes back.</summary>
    private static readonly byte[] VanillaImageBytes = AttackCardProbeText.EncodeWithTerminator(AttackCardText.VanillaDesc, 1);
}
