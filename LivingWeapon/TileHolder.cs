namespace LivingWeapon;

/// <summary>
/// Stateless write-path for the Treasure Master module. Separated from ArmAudit (the
/// read-path gate evaluator) per the read-path/write-path split: ArmAudit never writes,
/// TileHolder never evaluates arm gates.
///
/// <see cref="Hold"/> iterates each tile address in the map:
///   Writable guard -- skip on any failed guard.
///   U8 read        -- classify via <see cref="TreasureMaster.ClassifyAddr"/>.
///   Resting        -- W8(WantWrite) if cur differs.
///   Held           -- already marked, nothing to do.
///   Foreign        -- counted but never written (off-screen render bytes from camera pan
///                     or action camera; they return to Resting when the tile scrolls back).
///
/// Returns (written, foreign) so the caller can log on first occurrence of foreign bytes
/// without needing to re-read memory.
/// </summary>
internal sealed class TileHolder
{
    private readonly IGameMemory _mem;

    public TileHolder(IGameMemory mem) => _mem = mem;

    /// <summary>
    /// Executes one hold pass over all tile addresses in <paramref name="map"/>.
    /// Returns the number of addresses written and the number of foreign addresses seen.
    /// Foreign addresses are never written; the caller decides what to do with the count.
    /// </summary>
    internal (int written, int foreign) Hold(TreasureMap map)
    {
        int written = 0, foreign = 0;
        foreach (var tile in map.Tiles)
        {
            foreach (var (addr, _) in tile.Addrs)
            {
                if (!_mem.Writable(addr, 1)) continue;
                byte cur = _mem.U8(addr);
                switch (TreasureMaster.ClassifyAddr(cur))
                {
                    case TreasureMaster.AddrState.Resting:
                        byte want = TreasureMaster.WantWrite(cur);
                        if (cur != want) { _mem.W8(addr, want); written++; }
                        break;
                    case TreasureMaster.AddrState.Held:
                        break;
                    case TreasureMaster.AddrState.Foreign:
                        foreign++;
                        break;
                }
            }
        }
        return (written, foreign);
    }


}
