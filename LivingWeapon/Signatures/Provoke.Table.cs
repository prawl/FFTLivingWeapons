namespace LivingWeapon;

/// <summary>
/// Provoke's table lifecycle. The byte writes that repoint ability 189's effect (mechanism and
/// LIVE_LEDGER provenance are in ProvokePolicy's class doc). Separate from the slot lifecycle in
/// Provoke.cs.
///
/// Keyed on wielder existence (wielderSlot &gt;= 0 in Provoke.Tick), not job eligibility. Tying the
/// restore to slot release would let a Knight-Dragoon-Knight job flip repoint 189 back and forth,
/// so a queued cast could resolve against the wrong row.
///
/// All seven bytes (action byte + six inflict-row bytes) go through mem.U8/W8 one at a time,
/// guarded by Readable then Writable; the test fake's WriteBytes does not update U8 reads. A
/// refused write is logged (docs/PROVOKE_AC.md criterion 17) and not assumed to have landed.
/// </summary>
internal sealed partial class Provoke
{
    // Original bytes from the first successful capture, restored on release. Cleared after a
    // restore so a later grant recaptures.
    private bool _tableCaptured;
    private byte _origActionByte;
    private readonly byte[] _origInflictRow = new byte[ProvokePolicy.InflictStride];

    // Latch for the bounds refusal below (the log flood shape): without it a bad ability id in
    // items.json would warn every tick. Same pattern as Barrage's _noSlotLogged.
    private bool _boundsRefusedLogged;

    /// <summary>Write the authored inflict row and the action byte that points at it. Writes
    /// nothing when every byte is already correct.</summary>
    private void WriteTable(int abilityId)
    {
        // Refuse a bad id before deriving any address from it (see IsValidAbilityId).
        if (!ProvokePolicy.IsValidAbilityId(abilityId) || !ProvokePolicy.IsValidInflictRow(ProvokePolicy.ProvokeInflictRow))
        {
            if (Signatures.StuckEdge(ref _boundsRefusedLogged, true))
                ModLogger.WarnWithTrace(LogVerb.Grant,
                    "Provoke's granted ability id is out of range for the action table; refusing to touch it",
                    $"provoke bounds refused (ability {abilityId}, inflict row {ProvokePolicy.ProvokeInflictRow})");
            return;
        }
        Signatures.StuckEdge(ref _boundsRefusedLogged, false);   // back in range -> re-arm for next time

        long actionAddr = ProvokePolicy.ActionInflictAddr(abilityId);
        long inflictAddr = ProvokePolicy.InflictRowAddr(ProvokePolicy.ProvokeInflictRow);
        CaptureOriginal(actionAddr, inflictAddr);

        // Never write bytes we cannot restore. If the capture was partial, bail.
        if (!_tableCaptured) return;

        // Author the inflict row first and repoint the action byte last, so the ability never
        // resolves against an unauthored row. The game is multi-threaded.
        for (int i = 0; i < ProvokePolicy.InflictStride; i++)
        {
            long addr = inflictAddr + i;
            if (!_mem.Readable(addr, 1)) continue;
            byte cur = _mem.U8(addr);
            if (ProvokePolicy.NeedsInflictByteWrite(cur, i))
                TryWriteByte(addr, ProvokePolicy.DesiredInflictRow[i], "provoke inflict-row");
        }
        if (_mem.Readable(actionAddr, 1))
        {
            byte cur = _mem.U8(actionAddr);
            if (ProvokePolicy.NeedsActionWrite(cur))
                TryWriteByte(actionAddr, (byte)ProvokePolicy.ProvokeInflictRow, "provoke action-byte");
        }
    }

    /// <summary>Put back the bytes captured before the first write. A refused restore is benign
    /// (the image reloads clean next launch) but is logged.</summary>
    private void RestoreTable(int abilityId)
    {
        if (!_tableCaptured) return;
        long actionAddr = ProvokePolicy.ActionInflictAddr(abilityId);
        long inflictAddr = ProvokePolicy.InflictRowAddr(ProvokePolicy.ProvokeInflictRow);
        bool allRestored = true;

        if (_mem.Readable(actionAddr, 1) && _mem.U8(actionAddr) != _origActionByte)
            allRestored &= TryWriteByte(actionAddr, _origActionByte, "provoke action-byte restore");
        for (int i = 0; i < ProvokePolicy.InflictStride; i++)
        {
            long addr = inflictAddr + i;
            if (_mem.Readable(addr, 1) && _mem.U8(addr) != _origInflictRow[i])
                allRestored &= TryWriteByte(addr, _origInflictRow[i], "provoke inflict-row restore");
        }

        // Forget the originals only once every byte restored. After a refused write the repointed
        // byte is still in the table, and clearing the flag would let the next CaptureOriginal
        // snapshot it (0x1D / the authored 80 80 00 00 00 00 row) as the game's own, stranding the
        // repoint. Unlike RestoreSlot's unconditional clear (Provoke.cs), which verifies before
        // writing, a refusal here means our repoint is still live, so the state is held for retry.
        if (allRestored) _tableCaptured = false;
    }

    /// <summary>Snapshot the pre-repoint bytes once, and only when every byte is readable.</summary>
    private void CaptureOriginal(long actionAddr, long inflictAddr)
    {
        if (_tableCaptured || !_mem.Readable(actionAddr, 1)) return;
        for (int i = 0; i < ProvokePolicy.InflictStride; i++)
            if (!_mem.Readable(inflictAddr + i, 1)) return;
        _origActionByte = _mem.U8(actionAddr);
        for (int i = 0; i < ProvokePolicy.InflictStride; i++) _origInflictRow[i] = _mem.U8(inflictAddr + i);
        _tableCaptured = true;
    }

    /// <summary>Returns whether the write landed, so RestoreTable can hold its originals until a
    /// retry succeeds.</summary>
    private bool TryWriteByte(long addr, byte value, string what)
    {
        if (!_mem.Writable(addr, 1))
        {
            ModLogger.WarnWithTrace(LogVerb.Grant, "Could not write one of Provoke's table bytes; the guarded write was refused",
                $"{what} write refused (addr {addr:X})");
            return false;
        }
        _mem.W8(addr, value);
        return true;
    }
}
