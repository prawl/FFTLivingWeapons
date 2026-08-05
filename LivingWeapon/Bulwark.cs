using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Sunderer's "Bulwark" signature ("Line in the Sand", docs/BULWARK_AC.md): at tier three, the
/// wielder's FULL WAIT turn (no move, no act -- Mushin's exact falling-edge shape, see Mushin.cs's
/// class doc for the full PSX turn-flag provenance) plants the blade. While the stance holds, the
/// ONE tile directly BEHIND the wielder is barred until the wielder's next turn opens, dies, or
/// cannot be located -- "the anti-Provoke, the leave-me-alone move": the wielder keeps its own
/// mobility, allies are never walled, and denying the back tile denies the back-attack bonus. THIS
/// FILE is the trigger/edge-tracking half (ctor, gate chain, the turn-flag edge machinery);
/// Bulwark.Terrain.cs is the terrain read/write half (Plant/MaintainPlant/Release/BuildOccupancy),
/// split the same way ISignature modules elsewhere split a stateful half from a pure Policy file --
/// here BOTH halves are stateful, so the seam is "decides" vs. "touches memory", not "pure vs
/// impure".
///
/// THE MECHANISM (settled live 2026-07-28, LIVE_LEDGER Contradicted-section terrain entry -- the
/// ORIGINAL height-based design below is DEAD): the pathfinder's live terrain grid
/// (Offsets.PathTerrainGrid, base CORRECTED 2026-07-28 to 0x140D8DCB0) exposes the engine's OWN
/// obstacle state at byte +6 bit 0x02 (Offsets.PathTerrainVetoBit) -- this map's five natural
/// trees all read byte+6 == 0x22, bit 0x02 set. OR'ing it in blocks movement AND enemy AI pathing
/// while the tile stays hoverable/selectable, rendering the game's own red circle-slash "invalid
/// destination" cursor -- a native affordance, not a cosmetic compromise (bit 0x01 also blocks but
/// additionally strips the cursor mask, rejected). HEIGHT (byte +2, the original mechanism) is NOT
/// a walkability input: a raised vacant tile stayed selectable and SOFTLOCKED the game when a unit
/// stepped onto it. ALLIES ARE BLOCKED TOO -- the obstacle bit does not discriminate by side,
/// matching vanilla terrain's own behavior (a tree blocks everyone). Occupancy is read LAYER-BLIND
/// (BuildOccupancy, Bulwark.Terrain.cs, ignores which pathfinder layer a seat's (gx,gy) belongs
/// to), the conservative-safe direction under a bridge: a tile genuinely vacant on the wielder's
/// own layer but reading "occupied" because a unit sits on the deck above/below only ever loses
/// the one block it could have taken, never blocks an occupied one.
///
/// FACING (Offsets.ALayerBit low 2 bits, live-proven to track the Wait facing wheel 2026-07-28,
/// same ledger cite) is read together with the layer bit in ONE byte read, exactly once, at the
/// falling turn-flag edge -- the instant the player has just finished choosing facing. A
/// battle-teardown zero-sweep can transiently read facing 0 (South); accepted rather than guarded
/// against, because a spurious teardown plant self-heals through the ResetBattle restore (A1).
///
/// GRID WRITES PERSIST THE WHOLE PROCESS SESSION (proven 2026-07-28: stale walkability dirt once
/// crashed the game), so restore is mandatory on every path that ends a hold, INCLUDING battle
/// exit -- see ResetBattle's A1 below.
///
/// CTOR INJECTS BannerToast (a NEW pattern versus Mushin/Sanctuary/Renewal's mem-only ctors):
/// Engine constructs _toast before any signature module, so passing it in here is safe -- see
/// Engine.cs's ctor ordering (_toast is built immediately after LaunchGuard/AnchorScout, long
/// before the signature modules further down).
///
/// A1 (<see cref="ResetBattle"/>, THE BATTLE-EDGE CONTRACT, INVERTED 2026-07-28 after the crash):
/// grid writes persist the whole process session, so the battle edge is a RELEASE path, not a
/// drop. Engine fires ResetBattle on BOTH the enter and exit edges (Engine.ResetBattleState doc):
/// the exit edge is what actually restores a still-active hold (write every saved original back
/// through the same guarded path Release uses, THEN clear all state); the following enter-edge
/// call is an idempotent no-op on an already-empty book (Release itself no-ops and stays silent
/// when there is nothing to restore, so the routine double-edge reset produces no log noise). The
/// restore is safe on ANY edge because the grid is one persistent per-process structure and the
/// book holds this same session's true originals -- there is no "wrong map" to scribble onto.
/// </summary>
internal sealed partial class Bulwark : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.OnField);

    internal const int SundererId = 50;

    private readonly IGameMemory _mem;
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly BannerToast _toast;
    private readonly ScopedLogger _slog;   // armed gate: a benched/below-tier Sunderer must not narrate on console

    private bool _wasActive;
    /// <summary>Previous TURN FLAG value. Single wielder (LW-136 refuses ambiguity, see Tick), so
    /// one nullable int suffices -- absence means "not yet primed", mirroring Mushin.cs exactly:
    /// the very next observed value is captured WITHOUT deciding.</summary>
    private int? _prevTurnFlag;
    private bool _planted;
    /// <summary>Grid idx -&gt; the ORIGINAL f6 byte, for the tile currently held (at most one, the
    /// single behind tile). Restored exactly on release, INCLUDING from <see cref="ResetBattle"/>
    /// (see the class doc's A1 -- the battle edge is a release path, not a drop).</summary>
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
        // A1: restore BEFORE clearing -- see the class doc. Release() itself no-ops (no writes,
        // no log) when the book and deferred watch are already empty, so the routine enter-edge
        // call that follows every exit-edge reset stays silent.
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
        // LW-136: ResolveDeployedMainHand returns 0 on ZERO or on TWO-OR-MORE deployed wielders
        // (refuse rather than guess -- two planters would contest tiles with colliding restore
        // books). AC criterion 3 / plan amendment A2.
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
            _prevTurnFlag = null;   // re-prime clean whenever a wielder resolves again (no leaked edge)
            if (_planted)
            {
                MaintainPlant();   // the hold survives a transient miss
                if (++_unresolvedStreak >= Tuning.BulwarkUnresolvedTicks)
                    Release("the wielder could not be located");
            }
            return;
        }
        _unresolvedStreak = 0;

        // A7: wielder loss is explicit machinery -- a corpse never raises the turn flag, so this
        // is the only release path death takes.
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
                else if (prev == 1 && cur == 0 && active)   // falling edge decides once, mirrors Mushin
                {
                    bool moved = _mem.Readable(entry + Offsets.AMoved, 1) && _mem.U8(entry + Offsets.AMoved) != 0;
                    bool acted = _mem.Readable(entry + Offsets.AActed, 1) && _mem.U8(entry + Offsets.AActed) != 0;
                    if (BulwarkPolicy.ShouldPlant(turnEnded: true, moved, acted))
                        Plant(entry);
                }
            }
            else
            {
                _prevTurnFlag = cur;   // first sight: prime only, never decide
            }
        }

        if (_planted) MaintainPlant();
    }
}
