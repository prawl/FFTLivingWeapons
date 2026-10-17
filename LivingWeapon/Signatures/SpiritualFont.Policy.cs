using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The pure decisions behind Umbral Rod's "Spiritual Font" signature -- no memory access.
/// The stateful position-poll watcher and guarded writes live in SpiritualFont.cs.
/// </summary>
internal sealed partial class SpiritualFont
{
    /// <summary>True when the signature is configured (FontOnMove set) and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.FontOnMove;

    /// <summary>Main-hand only: the weapon must be in RRHand to activate, though kills count from any hand.</summary>
    public const bool ActivatesOnMainHandOnly = true;

    /// <summary>New MP after the gain, clamped at maxMp. Unlike the HP half, mp 0 still gains (HP 0
    /// to positive is the revival signal). No-op on junk maxMp or a non-positive gain.</summary>
    public static int NewMp(int mp, int maxMp, int gain)
    {
        if (maxMp <= 0 || gain <= 0) return mp;
        int n = mp + gain;
        return n > maxMp ? maxMp : n;
    }

    /// <summary>The MP half runs only for a living wielder on a validated layout. One who died
    /// after moving gains nothing, matching the HP half (BandHeal.NewHp never revives).</summary>
    public static bool MpHalfAllowed(int hp, bool mpOk) => mpOk && hp > 0;

    /// <summary>Per-battle layout check gating every MP write. The band +0x18/+0x1A pair is
    /// provisional and unconfirmed, so require at least 2 sampled units, mp &lt;= maxMp and
    /// maxMp &lt;= 999 for all, and maxMp &gt;= 1 for one. A fail means HP-only that battle.</summary>
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

    /// <summary>Guarded u16 write of the wielder's MP at the provisional +0x18 on its band entry;
    /// no-op if not writable. One W16 avoids a torn value between two W8 halves (LW-145 fix 2, as BandHeal.WriteHp).</summary>
    public static void WriteMp(IGameMemory mem, long entryAddr, int newMp)
    {
        long a = entryAddr + Offsets.AMp;
        if (!mem.Writable(a, 2)) return;
        mem.W16(a, (ushort)newMp);
    }

    // MoveWatch: position-poll state machine. The first sighting baselines silently. A new tile must
    // hold for StabilityTicks ticks before it fires (rides out mid-animation pulses and the (0,0)
    // flicker on the live band), then RateCap suppresses further fires. Knockback and teleport also
    // pay, like vanilla fonts.

    /// <summary>Consecutive ticks a new position must hold before it counts; filters band flicker.</summary>
    public const int StabilityTicks = 3;

    /// <summary>Ticks to suppress fires after one (~3 s at 33 ms), giving one move per turn.</summary>
    public const int RateCap = 90;

    /// <summary>Position-poll state machine for the Spiritual Font moved-turn trigger.</summary>
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

        /// <summary>Feed the current tile. True once per completed move.</summary>
        public bool Observe(int gx, int gy)
        {
            // Cooldown: count down, never fire.
            if (_state == State.Cooldown)
            {
                if (--_coolTicks <= 0) _state = State.Stable;
                // Keep the baseline current so a second move registers after the cap.
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

            if (gx == _baseGx && gy == _baseGy)
            {
                // Back at baseline: cancel any candidate.
                _stabCount = 0;
                _candGx = _candGy = 0;
                _state = State.Stable;
                return false;
            }

            if (_state == State.Stable || (gx != _candGx || gy != _candGy))
            {
                // Start or restart the stability count.
                _candGx = gx; _candGy = gy;
                _stabCount = 1;
                _state = State.Candidate;
                return false;
            }

            _stabCount++;
            if (_stabCount >= StabilityTicks)
            {
                // Confirmed: fire and enter cooldown.
                _baseGx = gx; _baseGy = gy;
                _stabCount = 0;
                _candGx = _candGy = 0;
                _coolTicks = RateCap;
                _state = State.Cooldown;
                return true;
            }
            return false;
        }

        /// <summary>Set the baseline without firing (used during cooldown).</summary>
        private void AcceptBaseline(int gx, int gy) { _baseGx = gx; _baseGy = gy; }

        // Exposed for tests only.
        internal bool IsFresh => _state == State.Fresh;
        internal bool IsStable => _state == State.Stable;
        internal bool IsCandidate => _state == State.Candidate;
        internal bool IsCooldown => _state == State.Cooldown;
        internal int CoolTicks => _coolTicks;
        internal int StabCount => _stabCount;
    }
}
