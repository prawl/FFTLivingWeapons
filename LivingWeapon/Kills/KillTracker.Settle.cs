namespace LivingWeapon;

internal sealed partial class KillTracker
{
    /// <summary>At the battle exit edge, pays corpses seen dying on the field whose dead streak the
    /// victory screen cut short. Returns true if the tally changed.</summary>
    public bool SettleCorpsesAtBattleEnd()
    {
        bool changed = false;
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Band.Entry(s);
            if (!Band.IsValid(_mem, addr)) continue;

            var slot = ReadSlot(addr);
            if (!slot.IsDead) continue;

            if (Settleable(s, in slot))
                _recorder?.Invoke("kill", $"settle-at-exit slot={s} dead-ticks={_deadStreak[s]}");
            ScanDeadPath(s, onField: false, in slot, ref changed, settling: true);
        }
        return changed;
    }

    // Shared by the exit tape line and the settling guard so the two cannot drift apart.
    private bool Settleable(int s, in SlotSnapshot slot) =>
        !_deadCredited[s] && _seenAlive[s]
        && _slotId[s].lvl == slot.Lvl && _slotId[s].br == slot.Br && _slotId[s].fa == slot.Fa
        && _deadStreak[s] >= 1 && _deadStreak[s] < DeadNeeded;
}
