using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The pure decisions behind Umbral Rod's "Spiritual Font" signature -- no memory access.
/// The stateful position-poll watcher and guarded writes live in SpiritualFont.cs.
/// </summary>
internal sealed partial class SpiritualFont
{
    /// <summary>True when the signature is configured and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
    {
        if (sig is null || !sig.FontOnMove) return false;
        return tier >= sig.AtTier;
    }

    /// <summary>Wielder resolution is main-hand-only: the weapon must be in RRHand to activate.
    /// A Living Weapon earns kills in any hand, but commands its gift only from the main hand.</summary>
    public const bool ActivatesOnMainHandOnly = true;

    /// <summary>New MP after the gain: clamped at maxMp. UNLIKE the HP half, mp 0 still gains --
    /// an empty pool is not a corpse (HP 0 -&gt; positive is the engine's revival signal; MP has
    /// no such semantics). No-op on junk maxMp or a non-positive gain.</summary>
    public static int NewMp(int mp, int maxMp, int gain)
    {
        if (maxMp <= 0 || gain <= 0) return mp;
        int n = mp + gain;
        return n > maxMp ? maxMp : n;
    }

    /// <summary>The MP half runs only for a LIVING wielder on a layout-proven battle: a wielder
    /// who moved and then died before their turn edge (trap tile, counter-kill) gains NOTHING --
    /// the HP half already no-ops at hp 0 (LifeSap.NewHp never revives), and MP must not be
    /// written into a corpse either, even though MP carries no revival semantics.</summary>
    public static bool MpHalfAllowed(int hp, bool mpOk) => mpOk && hp > 0;

    /// <summary>The PURE per-battle layout validation gating EVERY MP write: the band +0x18/+0x1A
    /// pair is PROVISIONAL (never live-verified), so before the first MP write of a battle the
    /// whole band must look like MP -- at least 2 sampled units, mp &lt;= maxMp AND maxMp &lt;= 999
    /// for ALL of them, and maxMp &gt;= 1 for at least one (an all-zero sweep proves nothing).
    /// A fail means HP-only for that battle; the HP half is never gated.</summary>
    public static bool MpLayoutOk(IReadOnlyList<(int mp, int maxMp)> units)
    {
        if (units.Count < 2) return false;
        bool anyPool = false;
        foreach (var (mp, maxMp) in units)
        {
            if (mp > maxMp || maxMp > 999) return false;
            if (maxMp >= 1) anyPool = true;
        }
        return anyPool;
    }

    /// <summary>Guarded little-endian u16 write of the wielder's MP on its band entry (the
    /// provisional +0x18). Fail-safe no-op when the page isn't writable -- LifeSap.WriteHp's
    /// shape. The caller re-reads afterwards and logs SET/MISS.</summary>
    public static void WriteMp(long entryAddr, int newMp)
    {
        long a = entryAddr + Offsets.AMp;
        if (!Mem.Writable(a, 2)) return;
        Mem.W8(a, (byte)(newMp & 0xFF));
        Mem.W8(a + 1, (byte)((newMp >> 8) & 0xFF));
    }

    // -------------------------------------------------------------------------
    // MoveWatch: pure position-poll state machine.
    //
    // Per tick (in-battle, active, located): caller feeds the current (gx, gy).
    //   • FIRST sighting / after reset: baselines silently -- never fires.
    //   • CHANGE detected: must be stable for StabilityTicks consecutive ticks
    //     before firing -- rides out mid-animation position pulses and the
    //     documented (0,0)-flicker on the live band.
    //   • After firing: suppressed for RateCap ticks (~3 s at 33 ms) to give
    //     one-move-per-turn semantics. Knockback / teleport also pays (intended;
    //     vanilla fonts pay on any movement).
    //   • RESET (unequip / battle end): returns to the fresh state.
    // -------------------------------------------------------------------------

    /// <summary>Consecutive-tick stability required before treating a new position as final.
    /// Filters the documented mid-animation band-position flicker.</summary>
    public const int StabilityTicks = 3;

    /// <summary>After a fire, suppress further fires for this many ticks (~3 s at 33 ms).
    /// One move per turn is the realistic cadence; this also limits knockback spam.</summary>
    public const int RateCap = 90;

    /// <summary>
    /// Pure position-poll state machine for the "Spiritual Font" moved-turn trigger.
    /// No memory access; all state is internal so the caller owns the lifetime.
    /// </summary>
    internal sealed class MoveWatch
    {
        private enum State { Fresh, Stable, Candidate, Cooldown }

        private State _state = State.Fresh;
        private int _baseGx, _baseGy;       // the snapshotted (confirmed) position
        private int _candGx, _candGy;       // the new position being stability-counted
        private int _stabCount;             // ticks the candidate has been seen consecutively
        private int _coolTicks;             // ticks remaining in the post-fire cooldown

        /// <summary>Reset to the initial state (unequip or battle end).</summary>
        public void Reset()
        {
            _state = State.Fresh;
            _baseGx = _baseGy = 0;
            _candGx = _candGy = 0;
            _stabCount = 0;
            _coolTicks = 0;
        }

        /// <summary>Feed the current tile. Returns true exactly once per completed move
        /// (after 3 stable ticks on a new tile, outside the post-fire cooldown).</summary>
        public bool Observe(int gx, int gy)
        {
            // Cooldown: tick down; no fire; still accept a stable position as the new baseline.
            if (_state == State.Cooldown)
            {
                if (--_coolTicks <= 0) _state = State.Stable;
                // While cooling down, keep the snapshot current so a second consecutive move
                // is detected cleanly once the rate cap expires.
                AcceptBaseline(gx, gy);
                return false;
            }

            if (_state == State.Fresh)
            {
                // First sighting: baseline silently.
                _baseGx = gx; _baseGy = gy;
                _state = State.Stable;
                return false;
            }

            // State.Stable or State.Candidate:
            if (gx == _baseGx && gy == _baseGy)
            {
                // Returned to the baseline (or no movement at all): cancel any candidate.
                _stabCount = 0;
                _candGx = _candGy = 0;
                _state = State.Stable;
                return false;
            }

            // New position differs from the baseline.
            if (_state == State.Stable || (gx != _candGx || gy != _candGy))
            {
                // Start (or restart) stability count for this candidate.
                _candGx = gx; _candGy = gy;
                _stabCount = 1;
                _state = State.Candidate;
                return false;
            }

            // Continuing stability on the same candidate.
            _stabCount++;
            if (_stabCount >= StabilityTicks)
            {
                // Stable move confirmed: fire and enter cooldown.
                _baseGx = gx; _baseGy = gy;
                _stabCount = 0;
                _candGx = _candGy = 0;
                _coolTicks = RateCap;
                _state = State.Cooldown;
                return true;
            }
            return false;
        }

        /// <summary>Snapshot the current position as the baseline without firing (used
        /// during cooldown so back-to-back moves are detected correctly afterward).</summary>
        private void AcceptBaseline(int gx, int gy) { _baseGx = gx; _baseGy = gy; }

        // Exposed for tests only.
        internal bool IsFresh    => _state == State.Fresh;
        internal bool IsStable   => _state == State.Stable;
        internal bool IsCandidate => _state == State.Candidate;
        internal bool IsCooldown => _state == State.Cooldown;
        internal int  CoolTicks  => _coolTicks;
        internal int  StabCount  => _stabCount;
    }
}
