namespace LivingWeapon;

/// <summary>
/// Provoke's TABLE lifecycle: the two byte writes that repoint ability 189's effect (see
/// ProvokePolicy's class doc for the full mechanism and the LIVE_LEDGER provenance). A genuinely
/// separate state machine from the SLOT lifecycle in Provoke.cs, which is why it lives in its own
/// partial rather than being folded into Tick() there.
///
/// KEYED ONLY ON WIELDER EXISTENCE (wielderSlot &gt;= 0 in Provoke.Tick), never on job eligibility
/// -- the correction from plan review that makes this its own state machine. If the table restore
/// instead rode the slot-release path, a Knight-to-Dragoon-to-Knight job flip would repoint ability
/// 189 back to its vanilla effect (Dragoon can't receive Provoke, so the slot half releases) and
/// forward again on the next flip, opening a window where a queued cast on that ability id resolves
/// against the wrong table row. As long as SOME tier-3 main-hand Defender wielder exists anywhere
/// in the roster, the repoint stays up regardless of who can currently receive the command slot.
///
/// PER-BYTE GUARDED, NEVER BLOCK I/O: every one of the seven bytes (the action byte + the six
/// authored inflict-row bytes) is read and written individually via mem.U8/W8, guarded by
/// Readable then Writable. NOT WriteBytes/TryReadBytes -- the test fake's WriteBytes does not
/// update what U8 reads, which would make an idempotence test unable to observe its own write.
/// A refused write is logged (docs/PROVOKE_AC.md criterion 17: a refused guarded write must log
/// distinctly) and never assumed to have succeeded.
/// </summary>
internal sealed partial class Provoke
{
    // Bookkeeping: the ORIGINAL bytes at the first successful capture, so release can restore
    // them. Cleared once restored so a later grant recaptures fresh rather than trusting a stale
    // snapshot from an earlier session's read.
    private bool _tableCaptured;
    private byte _origActionByte;
    private readonly byte[] _origInflictRow = new byte[ProvokePolicy.InflictStride];

    // Signatures.StuckEdge latch for the bounds refusal below -- LW-69's flood shape (a per-tick
    // warning with no latch bloats the file sink at ~30Hz; see Barrage's own _noSlotLogged/
    // _recNotReadableLogged for the established pattern this copies). A typo'd items.json ability
    // id would otherwise re-log this warning on every tick for the rest of the session.
    private bool _boundsRefusedLogged;

    /// <summary>Write (idempotently) the authored inflict row and the action byte that points at
    /// it. A tick that finds every byte already correct performs zero writes.</summary>
    private void WriteTable(int abilityId)
    {
        // Bounds-check BEFORE computing or touching anything derived from abilityId -- a bad id
        // must be refused and logged, not used to compute an address past the table (see
        // IsValidAbilityId's doc for why). Refusing here also means CaptureOriginal below is
        // never asked to read/remember bytes at a bogus address.
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

        // Writing bytes we cannot restore is the one thing this module must never do (see the
        // class doc's PER-BYTE GUARDED invariant). If the capture above did not fully succeed
        // (some byte was unreadable), _tableCaptured stays false and RestoreTable can never put
        // these bytes back -- so bail before writing anything at all.
        if (!_tableCaptured) return;

        // Data before pointer: author the inflict row FIRST, then repoint the action byte at it
        // LAST, so ability abilityId can never resolve against a row that has not been authored
        // yet. Benign today (an unauthored row 29 reads mode 0, which inflicts nothing), but the
        // game is multi-threaded and this ordering should not depend on that staying true.
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

    /// <summary>Put back the bytes captured before our first write. A refused restore is benign
    /// (the image reloads clean next launch) but is still attempted and still logged -- never
    /// assumed to have succeeded.</summary>
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

        // Only forget the captured originals once every byte that needed restoring actually got
        // restored. A refused write (Writable false, e.g. a write-protected page) means the
        // repointed byte is STILL sitting in the table -- clearing the flag here anyway would let
        // the next WriteTable's CaptureOriginal snapshot that ALREADY-REPOINTED byte (0x1D / the
        // authored 80 80 00 00 00 00 row) as though it were the game's own original, stranding the
        // repoint for the rest of the session (the exact bug this guards against). Leaving
        // _tableCaptured true keeps the real originals remembered so a later tick can retry.
        //
        // This is deliberately asymmetric with RestoreSlot's unconditional _state.Clear()
        // (Provoke.cs): that path verifies BEFORE writing (Barrage.ReleaseSlot checks the current
        // byte is still ours), so a refusal there means someone else already owns the slot and
        // there is nothing left of ours to protect. Here a refusal means the opposite -- our
        // repoint is still live and unrestored -- so the state must be held, not dropped.
        if (allRestored) _tableCaptured = false;
    }

    /// <summary>Snapshot the pre-repoint bytes exactly once, and only once every byte is readable
    /// -- a partial capture would leave RestoreTable unable to put back bytes it never recorded.</summary>
    private void CaptureOriginal(long actionAddr, long inflictAddr)
    {
        if (_tableCaptured || !_mem.Readable(actionAddr, 1)) return;
        for (int i = 0; i < ProvokePolicy.InflictStride; i++)
            if (!_mem.Readable(inflictAddr + i, 1)) return;
        _origActionByte = _mem.U8(actionAddr);
        for (int i = 0; i < ProvokePolicy.InflictStride; i++) _origInflictRow[i] = _mem.U8(inflictAddr + i);
        _tableCaptured = true;
    }

    /// <summary>Returns whether the write actually landed, so RestoreTable can tell a refusal
    /// apart from success and hold onto its captured originals until a retry succeeds.</summary>
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
