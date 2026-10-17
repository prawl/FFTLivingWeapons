using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Sunderer's "Bulwark" signature ("Line in the Sand", docs/BULWARK_AC.md). At tier three a full
/// wait turn (no move, no act, the same falling edge as Mushin.cs) plants the blade, and the tile
/// behind the wielder is barred until the wielder's next turn opens, dies, or cannot be located.
/// This file holds the trigger and edge tracking; Bulwark.Terrain.cs reads and writes the grid.
///
/// Mechanism (2026-07-28, LIVE_LEDGER Contradicted-section terrain entry; the earlier height design
/// is dead). The live terrain grid (Offsets.PathTerrainGrid, base 0x140D8DCB0) carries the engine's
/// obstacle bit at byte +6 bit 0x02 (Offsets.PathTerrainVetoBit); the map's five trees read 0x22.
/// Setting it blocks movement and enemy pathing but the tile stays selectable, so the game draws
/// its own invalid-destination cursor. Bit 0x01 also blocks but strips the cursor mask. Height
/// (byte +2) does not gate walking, and a raised vacant tile softlocked the game. Allies are
/// blocked too, like a tree. Occupancy is read layer-blind (BuildOccupancy), which under a bridge
/// can only lose a block, never block an occupied tile.
///
/// Facing (Offsets.ALayerBit low 2 bits) is read once with the layer bit at the falling turn-flag
/// edge. A teardown zero-sweep can read facing 0 (South); that plant self-heals via ResetBattle.
/// Grid writes persist for the whole process (stale walkability once crashed the game), so every
/// path that ends a hold restores the saved byte, including battle exit (A1, see ResetBattle).
/// The ctor takes BannerToast, which Engine builds before the signature modules.
/// </summary>
internal sealed partial class Bulwark : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.OnField);

    /// <summary>The Sunderer's item id. It was 264 from LW-351 stage 2 (2026-08-30), when it left the
    /// Slasher's slot (50) for an extended id, then 263 after the Moonblade's removal (2026-08-31).
    /// A save's tally follows it (TallyMigration, meta.json migratedFrom). BulwarkTests pins it.</summary>
    internal const int SundererId = 263;

    private readonly IGameMemory _mem;
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly BannerToast _toast;
    private readonly ScopedLogger _slog;   // armed gate keeps a benched or below-tier Sunderer off the console

    private bool _wasActive;
    /// <summary>Previous turn flag. LW-136 allows a single wielder, so one nullable int suffices;
    /// null means not yet primed, as in Mushin.cs.</summary>
    private int? _prevTurnFlag;
    private bool _planted;
    /// <summary>Grid idx -&gt; original f6 byte for the held tile (at most one, the tile behind).
    /// Restored on release, including from <see cref="ResetBattle"/> (A1).</summary>
    private readonly Dictionary<int, byte> _restoreBook = new();
    private readonly Dictionary<int, DeferredTile> _deferred = new();
    private int _unresolvedStreak;

    public Bulwark(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, BannerToast toast, IGameMemory? mem = null)
    {
        _mem = mem ?? new LiveMemory();
        _meta = meta;
        _kills = kills;
        _toast = toast;
        _slog = ModLogger.For(LogVerb.Signature, () => Wielder.AnyDeployedMainHand(_mem, SundererId));
    }

    public void ResetBattle()
    {
        // A1: restore before clearing. Release no-ops silently when the book and deferred watch are
        // empty, so the enter-edge call after each exit-edge reset logs nothing.
        Release("the battle ended");
        _wasActive = false;
        _prevTurnFlag = null;
        _unresolvedStreak = 0;
    }

    public void Tick(bool onField)
    {
        if (!onField) return;
        if (!_meta.TryGetValue(SundererId, out var m) || m.Signature is null || !m.Signature.Bulwark) return;

        int tier = Tuning.TierOf(_kills, SundererId);
        // Returns 0 for zero or two-or-more deployed wielders (two planters would contest
        // tiles with colliding restore books). AC criterion 3, plan amendment A2.
        long entry = Wielder.ResolveDeployedMainHand(_mem, SundererId, out _);
        bool active = tier >= m.Signature.AtTier && entry != 0;

        if (ActivationEdge.Step(ref _wasActive, active))
        {
            _slog.Info(active
                ? "Sunderer at tier three is wielded on the field; a full wait plants the blade and bars the ground behind the wielder."
                : "Bulwark is no longer active.");
        }

        if (entry == 0)
        {
            _prevTurnFlag = null;   // re-prime when a wielder resolves again, so no edge leaks
            if (_planted)
            {
                MaintainPlant();   // the hold survives a transient miss
                if (++_unresolvedStreak >= Tuning.BulwarkUnresolvedTicks)
                    Release("the wielder could not be located");
            }
            return;
        }
        _unresolvedStreak = 0;

        // A7: a corpse never raises the turn flag, so this is the release path for death.
        if (_planted)
        {
            int hp = _mem.U16(entry + Offsets.AHp);
            bool deadBit = (_mem.U8(entry + Offsets.ADeadStatus) & Offsets.ADeadBit) != 0;
            if (hp == 0 || deadBit) { Release("the wielder fell"); return; }
        }

        if (_mem.Readable(entry + Offsets.ATurnFlag, 1))
        {
            int cur = _mem.U8(entry + Offsets.ATurnFlag);
            if (_prevTurnFlag is int prev)
            {
                _prevTurnFlag = cur;
                if (BulwarkPolicy.ShouldRelease(prev, cur) && _planted)
                {
                    Release("the wielder's next turn opened");
                }
                else if (prev == 1 && cur == 0 && active)   // falling edge decides once, as in Mushin
                {
                    bool moved = _mem.Readable(entry + Offsets.AMoved, 1) && _mem.U8(entry + Offsets.AMoved) != 0;
                    bool acted = _mem.Readable(entry + Offsets.AActed, 1) && _mem.U8(entry + Offsets.AActed) != 0;
                    if (BulwarkPolicy.ShouldPlant(turnEnded: true, moved, acted))
                        Plant(entry);
                }
            }
            else
            {
                _prevTurnFlag = cur;   // first sight primes only
            }
        }

        if (_planted) MaintainPlant();
    }
}
