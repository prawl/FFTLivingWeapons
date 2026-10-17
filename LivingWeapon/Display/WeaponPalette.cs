using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The per-turn weapon-sprite palette repaint. A display subsystem, not an ISignature:
/// it has its own TickPhase row and Engine.ResetBattleState calls its ResetBattle().
/// On the acting unit's turn (<see cref="Band.FlagOwner"/>), reads its main-hand weapon id
/// (Offsets.AWeapon) and, if meta has bench colors for it (<see cref="WeaponMeta.Palette"/>/
/// <see cref="WeaponMeta.Colors"/>), writes entries 1..15 into both banks
/// (Offsets.WeaponPaletteBankA/B), restoring the first-paint snapshot when it no longer applies.
/// A FlagOwner refusal holds the current state (a stale color beats a wrong one). The decision
/// table is WeaponPalettePolicy.Decide; the LW-295 glow-tier read is in WeaponPalette.Desire.cs.
/// </summary>
internal sealed partial class WeaponPalette
{
    /// <summary>About 1s at the 33ms tick. Re-asserts the paint in case a battle load wiped the
    /// banks without a battle edge reaching this class (same cadence as GunSlinger, TickGates).</summary>
    internal const int ReassertTicks = 30;

    // A property, not a static readonly array: both banks are rebased pins, and a field
    // would freeze the image base at type-init.
    private static long[] Banks => new[] { Offsets.WeaponPaletteBankA, Offsets.WeaponPaletteBankB };

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly IGameMemory _mem;

    /// <summary>the shared kill tally (Engine._kills); Desire.cs keys the glow tier off it.</summary>
    private readonly Dictionary<int, int> _kills;

    /// <summary>Session lifetime, not cleared by ResetBattle: a battle load re-copies the vanilla
    /// file into the banks (see Offsets.WeaponPaletteBankA/B), so the first capture stays valid.
    /// Keyed by (bank, palette); entries 1..15, captured before the first paint of that pair.</summary>
    private readonly Dictionary<(long bank, int pal), ushort[]> _vanilla = new();

    /// <summary>what this runtime last wrote per (bank, palette), entries 1..15 with their
    /// bit 15s, to tell "still our colors" from a foreign write. Session lifetime like _vanilla.</summary>
    private readonly Dictionary<(long bank, int pal), ushort[]> _lastWritten = new();

    /// <summary>pairs already warned about this battle, so a foreign writer logs once.</summary>
    private readonly HashSet<(long bank, int pal)> _suspectWarned = new();

    private int _paintedPal = -1;
    private int _paintedWeapon = -1;
    private int _paintedGlow;
    private int _ticksUnchanged;

    public WeaponPalette(Dictionary<int, WeaponMeta> meta, IGameMemory mem, Dictionary<int, int> kills)
    {
        _meta = meta;
        _mem = mem;
        _kills = kills;
    }

    public void Tick(bool inLive)
    {
        if (!inLive) return;
        if (!Band.FlagOwner(_mem, out long entry, out _)) return;   // hold: no restore, no write

        int wid = _mem.U16(entry + Offsets.AWeapon);
        int desiredPal = -1, desiredWeapon = -1, desiredGlow = 0;
        WeaponMeta? desiredMeta = null;
        if (_meta.TryGetValue(wid, out var m) && m.Colors is { Length: 15 } && m.Palette is >= 0 and <= 15)
        {
            desiredPal = m.Palette;
            desiredWeapon = wid;
            desiredMeta = m;
            desiredGlow = DesiredGlow(_mem, entry, wid);
        }

        var action = WeaponPalettePolicy.Decide(_paintedPal, _paintedWeapon, _paintedGlow,
            desiredPal, desiredWeapon, desiredGlow, _ticksUnchanged, ReassertTicks);

        switch (action)
        {
            case WeaponPaletteAction.Nothing:
                if (_paintedWeapon >= 0) _ticksUnchanged++;
                break;
            case WeaponPaletteAction.Restore:
                RestoreBanks(_paintedPal, _paintedWeapon);
                _paintedPal = -1; _paintedWeapon = -1; _paintedGlow = 0; _ticksUnchanged = 0;
                break;
            case WeaponPaletteAction.Paint:
                PaintBanks(desiredPal, desiredWeapon,
                    WeaponPalettePolicy.BrightenAll(desiredMeta!.Colors!, Tuning.GlowFactors[desiredGlow]));
                _paintedPal = desiredPal; _paintedWeapon = desiredWeapon; _paintedGlow = desiredGlow; _ticksUnchanged = 0;
                break;
            case WeaponPaletteAction.RestoreThenPaint:
                RestoreBanks(_paintedPal, _paintedWeapon);
                PaintBanks(desiredPal, desiredWeapon,
                    WeaponPalettePolicy.BrightenAll(desiredMeta!.Colors!, Tuning.GlowFactors[desiredGlow]));
                _paintedPal = desiredPal; _paintedWeapon = desiredWeapon; _paintedGlow = desiredGlow; _ticksUnchanged = 0;
                break;
        }
    }

    /// <summary>Battle enter and exit edge: the banks were just refreshed from the file, so the
    /// latch is stale. _vanilla is session lifetime and stays.</summary>
    public void ResetBattle()
    {
        _paintedPal = -1;
        _paintedWeapon = -1;
        _paintedGlow = 0;
        _ticksUnchanged = 0;
        _suspectWarned.Clear();   // a new battle earns a fresh (single) warning per surface
    }

    /// <summary>Writes <paramref name="codes"/> (entries 1..15) into both banks at
    /// <paramref name="pal"/>, keeping each entry's current bit 15, and snapshots the pair on its
    /// first paint. A bank whose read fails is skipped. Must not mutate <paramref name="codes"/>:
    /// at tier 0 it is the shared meta Colors array.</summary>
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
            else
            {
                // On later paints, compare the bank to the snapshot and our last write. Matching
                // neither means a foreign writer or a bad first look; warn once per battle per surface and
                // keep the original snapshot so restores don't launder the wrong bytes.
                var now = new ushort[15];
                for (int i = 1; i <= 15; i++) now[i - 1] = cur[i];
                _lastWritten.TryGetValue(key, out var last);
                if (WeaponPalettePolicy.SnapshotSuspect(now, _vanilla[key], last) && _suspectWarned.Add(key))
                    ModLogger.Warn(LogVerb.Display, $"Palette {pal} bank 0x{bank:X} holds bytes matching " +
                        "neither vanilla nor our last paint; another writer touched it (or the first look " +
                        "was bad). Keeping the original snapshot.");
            }

            var outBytes = new byte[30];
            var written = new ushort[15];
            for (int i = 1; i <= 15; i++)
            {
                // Carry bit 15 from the bank and strip it from the baked code, so a corrupt
                // baked value cannot set it.
                ushort code = (ushort)((cur[i] & 0x8000) | (codes[i - 1] & 0x7FFF));
                written[i - 1] = code;
                outBytes[(i - 1) * 2] = (byte)(code & 0xFF);
                outBytes[(i - 1) * 2 + 1] = (byte)(code >> 8);
            }
            _mem.WriteBytes(tgt + 2, outBytes);
            _lastWritten[key] = written;
        }
        ModLogger.Debug(LogVerb.Display, $"Painted weapon {weaponId} into palette {pal}.");
    }

    /// <summary>Writes each bank's first-paint snapshot back, bit 15s included. A bank with no
    /// snapshot is left alone.</summary>
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
