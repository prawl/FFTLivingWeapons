using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-251: the per-turn weapon-sprite palette repaint. NOT an ISignature -- a display subsystem
/// (its own TickPhase row, an explicit ResetBattle() call from Engine.ResetBattleState, the
/// AttackCard.ResetBattle() precedent), because its job is cosmetic (which colours a swing shows),
/// not a kill-tier gameplay grant.
///
/// On the acting unit's turn (<see cref="Band.FlagOwner"/>), reads its main-hand weapon id
/// (Offsets.AWeapon) and, if meta carries authored bench colours for it
/// (<see cref="WeaponMeta.Palette"/>/<see cref="WeaponMeta.Colors"/>), writes those 15 codes
/// (entries 1..15; entry 0, transparency, is structurally out of the write window -- the write
/// always starts at the palette's own +2 byte offset) into BOTH resident palette banks
/// (Offsets.WeaponPaletteBankA/B) at that weapon's palette index, restoring the pre-first-paint
/// snapshot when the acting unit/weapon no longer wants that palette. See
/// WeaponPalette.Policy.cs's WeaponPalettePolicy.Decide for the full six-row decision table this
/// class only executes; this file is the stateful (memory-touching) half.
///
/// FlagOwner refusal (between turns, an ambiguous read, battle opening) HOLDS the current painted
/// state -- no restore, no write -- the same fail-safe direction every other FlagOwner-driven
/// module in this repo takes: a wrong colour would be worse than a merely stale one.
/// </summary>
internal sealed class WeaponPalette
{
    /// <summary>~1s at the 33ms engine tick (mirrors GunSlinger's own in-battle re-assert
    /// cadence, TickGates "gunslinger" row): self-heals a starved bracket where a battle-load or
    /// some other writer wipes the banks without this class ever seeing a battle edge.</summary>
    internal const int ReassertTicks = 30;

    private static readonly long[] Banks = { Offsets.WeaponPaletteBankA, Offsets.WeaponPaletteBankB };

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly IGameMemory _mem;

    /// <summary>Session-lifetime, NOT cleared by ResetBattle -- a battle load re-copies the same
    /// vanilla file bytes into the banks (Offsets.WeaponPaletteBankA/B's own doc), so the FIRST
    /// paint of a given session already captured the true vanilla bytes and a later battle's
    /// "vanilla" is identical. Keyed by (bank, palette); each value holds entries 1..15, captured
    /// the first time that pair is ever painted this session, before the write.</summary>
    private readonly Dictionary<(long bank, int pal), ushort[]> _vanilla = new();

    private int _paintedPal = -1;
    private int _paintedWeapon = -1;
    private int _ticksUnchanged;

    public WeaponPalette(Dictionary<int, WeaponMeta> meta, IGameMemory mem)
    {
        _meta = meta;
        _mem = mem;
    }

    public void Tick(bool inLive)
    {
        if (!inLive) return;
        if (!Band.FlagOwner(_mem, out long entry, out _)) return;   // hold: no restore, no write

        int wid = _mem.U16(entry + Offsets.AWeapon);
        int desiredPal = -1, desiredWeapon = -1;
        WeaponMeta? desiredMeta = null;
        if (_meta.TryGetValue(wid, out var m) && m.Colors is { Length: 15 } && m.Palette is >= 0 and <= 15)
        {
            desiredPal = m.Palette;
            desiredWeapon = wid;
            desiredMeta = m;
        }

        var action = WeaponPalettePolicy.Decide(_paintedPal, _paintedWeapon, desiredPal, desiredWeapon,
            _ticksUnchanged, ReassertTicks);

        switch (action)
        {
            case WeaponPaletteAction.Nothing:
                if (_paintedWeapon >= 0) _ticksUnchanged++;
                break;
            case WeaponPaletteAction.Restore:
                RestoreBanks(_paintedPal, _paintedWeapon);
                _paintedPal = -1; _paintedWeapon = -1; _ticksUnchanged = 0;
                break;
            case WeaponPaletteAction.Paint:
                PaintBanks(desiredPal, desiredWeapon, desiredMeta!.Colors!);
                _paintedPal = desiredPal; _paintedWeapon = desiredWeapon; _ticksUnchanged = 0;
                break;
            case WeaponPaletteAction.RestoreThenPaint:
                RestoreBanks(_paintedPal, _paintedWeapon);
                PaintBanks(desiredPal, desiredWeapon, desiredMeta!.Colors!);
                _paintedPal = desiredPal; _paintedWeapon = desiredWeapon; _ticksUnchanged = 0;
                break;
        }
    }

    /// <summary>Battle-enter AND battle-exit edge (Engine.ResetBattleState fires on both, the
    /// AttackCard.ResetBattle precedent): the banks were just refreshed from the loaded file, so
    /// the latch is stale by definition. _vanilla is session lifetime and is deliberately NOT
    /// cleared here.</summary>
    public void ResetBattle()
    {
        _paintedPal = -1;
        _paintedWeapon = -1;
        _ticksUnchanged = 0;
    }

    /// <summary>Writes <paramref name="codes"/> (entries 1..15) into both banks at
    /// <paramref name="pal"/>, carrying each entry's CURRENT bit 15 (never inventing it) and
    /// capturing the pre-write snapshot the first time this (bank, pal) pair is ever painted this
    /// session. A bank that fails TryReadBytes this tick is skipped entirely: no write, no
    /// snapshot (the other bank still paints).</summary>
    private void PaintBanks(int pal, int weaponId, int[] codes)
    {
        foreach (long bank in Banks)
        {
            long tgt = bank + pal * Offsets.WeaponPaletteStride;
            if (!_mem.TryReadBytes(tgt, Offsets.WeaponPaletteStride, out var raw)) continue;

            var cur = new ushort[16];
            for (int i = 0; i < 16; i++) cur[i] = (ushort)(raw[i * 2] | (raw[i * 2 + 1] << 8));

            var key = (bank, pal);
            if (!_vanilla.ContainsKey(key))
            {
                var snap = new ushort[15];
                for (int i = 1; i <= 15; i++) snap[i - 1] = cur[i];
                _vanilla[key] = snap;
            }

            var outBytes = new byte[30];
            for (int i = 1; i <= 15; i++)
            {
                ushort code = (ushort)((cur[i] & 0x8000) | codes[i - 1]);   // carry bit 15, never invent it
                outBytes[(i - 1) * 2] = (byte)(code & 0xFF);
                outBytes[(i - 1) * 2 + 1] = (byte)(code >> 8);
            }
            _mem.WriteBytes(tgt + 2, outBytes);
        }
        ModLogger.Debug(LogVerb.Display, $"Painted weapon {weaponId} into palette {pal}.");
    }

    /// <summary>Writes each bank's pre-first-paint snapshot back verbatim (its own bit 15s
    /// included) -- no read needed. A bank with no snapshot (never painted this session) is left
    /// untouched.</summary>
    private void RestoreBanks(int pal, int weaponId)
    {
        foreach (long bank in Banks)
        {
            if (!_vanilla.TryGetValue((bank, pal), out var snap)) continue;

            long tgt = bank + pal * Offsets.WeaponPaletteStride;
            var outBytes = new byte[30];
            for (int i = 0; i < 15; i++)
            {
                outBytes[i * 2] = (byte)(snap[i] & 0xFF);
                outBytes[i * 2 + 1] = (byte)(snap[i] >> 8);
            }
            _mem.WriteBytes(tgt + 2, outBytes);
        }
        ModLogger.Debug(LogVerb.Display, $"Restored weapon {weaponId}'s palette {pal} to vanilla.");
    }
}
