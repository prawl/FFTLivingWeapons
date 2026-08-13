namespace LivingWeapon;

/// <summary>
/// LW-31 stage 3's runtime half (AttackRow.Policy.cs's class doc has the full record geometry,
/// live-proven 2026-07-06): owns EVERY read/write of the live 36-byte Attack-command record.
/// Stateless per call, shape-based, no per-hit memory of its own, AttackCard.cs holds the
/// cache/anchor state (the current/previous split images) and calls these methods once per synced
/// hit. Mirrors the Iai.Policy.cs / Iai.cs split's shape (the stateful orchestrator lives
/// elsewhere; this class is the pure-shape-gated I/O boundary).
/// </summary>
internal sealed partial class AttackRow
{
    private readonly IGameMemory _mem;

    public AttackRow(IGameMemory mem) => _mem = mem;

    /// <summary>Reads the 36-byte record at LabelAddr - RecordGap and classifies its shape. NEVER
    /// writes. Unreadable when the record address itself cannot be read at all (a genuinely foreign
    /// heap layout, or a race with the buffer being freed).</summary>
    internal AttackRowShape Classify(long labelAddr)
    {
        long recordAddr = labelAddr - RecordGap;
        if (!_mem.TryReadBytes(recordAddr, RecordBytes, out var raw)) return AttackRowShape.Unreadable;

        var rec = AttackRecord.Parse(raw);
        if (IsVanillaShape(rec)) return AttackRowShape.Vanilla;
        if (IsOurShape(rec)) return AttackRowShape.Ours;
        return AttackRowShape.Foreign;
    }

    /// <summary>Writes a fresh split image: the caller has already verified the label bytes and
    /// that <paramref name="image"/> is a legal 74-byte footprint payload (AttackRow.Policy.BuildImage
    /// already enforces the length; this method trusts its caller on that, mirroring every other
    /// guarded-write site in this codebase). Writes the desc footprint FIRST, then the record's
    /// {nameOff, descOff} pair as ONE 8-byte write (the opposite order from <see cref="Restore"/>),
    /// so a read caught mid-transition never sees a record pointing at a still-vanilla footprint or
    /// vice versa in the wrong direction for each operation's own safe half. Only writes when
    /// <see cref="Classify"/> reads Vanilla or Ours (never Foreign/Unreadable); returns false
    /// (untouched) otherwise, or when either target span is not currently writable.</summary>
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

    /// <summary>Restores vanilla: only when <see cref="Classify"/> reads Ours. Writes the vanilla
    /// {nameOff, descOff} pair FIRST, then the flat vanilla 74-byte image: the opposite write order
    /// from <see cref="Paint"/>, the "strand killer" shape, since a record still Ours-shaped after
    /// this can never point at anything but the vanilla text once both writes land. When Classify
    /// reads Vanilla, the record is already correct and a text-only restore (if the footprint holds
    /// something other than vanilla) is the CALLER's decision, not this method's; see
    /// AttackCard.Paint.cs's SyncHit. Never writes on Foreign/Unreadable.</summary>
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
