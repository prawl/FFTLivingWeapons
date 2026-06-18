using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Galewind's "Puppeteer" signature (replaces Charm-Lock): while a +3 Galewind is the acting wielder's
/// main hand, the first dominatable ENEMY its action damages is PUPPETED -- the agency flag (combat
/// +0x05 / 0x08, band -0x17) is held SET so the player controls that enemy (its move + full skillset)
/// for PuppeteerTurns of the WIELDER's own turns (Larceny's proven clock), then reverts to AI.
///
/// ANTI-SNOWBALL: exactly ONE puppet at a time, and after one expires the wielder cannot dominate again
/// until Tuning.PuppeteerCooldownTurns of its OWN turns pass (TurnTracker -- the proven per-unit
/// acted-edge counter, Larceny's clock). TARGET GATE (⚠ ALLOW-EVERYONE): every struck enemy is
/// dominatable (Puppeteer.IsDominatable returns true) -- the job id is not consulted (it reads
/// unreliable story/special ids on IC); the enemy + fingerprint gates below are the real filter.
///
/// DETECTION mirrors Maim/Larceny: per-tick HP drops on enemy band slots during the wielder's acted
/// period (enemy-fingerprint filtered). HOLD + EXPIRE run EVERY in-battle tick. The puppet's turns are
/// counted off the global acted-edge (TurnTracker) -- NOT its CT: a player-controlled unit's CT (+0x25)
/// reads frozen, so CT-edge counting left the puppet stuck. BATTLE EXIT clears the bit and the state.
/// All reads/writes are VirtualQuery-guarded (Puppeteer.SetAgency).
///
/// Offsets confirmed live 2026-06-18: the agency flag (combat +0x05 / band -0x17) and the job-read
/// (JobOff == combat +0x03 / band -0x19) both read correct per-unit values in-game.
/// </summary>
internal sealed partial class Puppeteer : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.OnField);

    private const int GalewindId = 9;

    private readonly IGameMemory _mem;
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly KillTracker _tracker;
    private readonly TurnTracker _turns;
    private readonly PuppeteerState _state;
    private readonly RicochetState _hpState;   // HP-diff baseline (same pattern as Maim/Ricochet)
    private bool _wasActive;

    public Puppeteer(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, KillTracker tracker,
                     TurnTracker turns, IGameMemory? mem = null)
    {
        _mem = mem ?? new LiveMemory();
        _meta = meta;
        _kills = kills;
        _tracker = tracker;
        _turns = turns;
        _state = new PuppeteerState();
        _hpState = new RicochetState(Offsets.BandSlots);
    }

    public void ResetBattle()
    {
        Release();            // clear the agency bit on any active puppet (revert to AI) before clearing
        _state.Clear();
        _wasActive = false;
        _hpState.ResetBattle();
    }

    /// <summary>The fingerprint of the active puppet, or null (test/inspection hook).</summary>
    internal (int mhp, int lvl, int br, int fa)? PuppetFingerprint => _state.Fingerprint;

    public void Tick(bool onField)
    {
        if (!_meta.TryGetValue(GalewindId, out var m) || m.Signature is null) { Drive(); return; }
        int tier = Tuning.TierOf(_kills, GalewindId);
        int puppetTurns = m.Signature.PuppeteerTurns;

        bool active = onField && IsActive(m.Signature, tier)
                      && Signatures.IsActingMainHand(_tracker.LastPlayerMainHand, GalewindId)
                      && _mem.U8(Offsets.Acted) == 1;
        if (active != _wasActive)
        {
            _wasActive = active;
            Log.Info($"puppeteer {(active ? "ACTIVE -- Galewind wielder is acting; the next struck enemy becomes your puppet" : "inactive")}");
        }

        // Latch a NEW puppet: on-field, wielder acting, none held, off cooldown. The HP-diff baseline
        // (Observe) is maintained on EVERY on-field tick regardless, so detection survives idle gaps.
        if (onField)
        {
            // Cooldown clock = TurnTracker.GlobalTurns (a monotonic battle-turn counter), NOT per-unit
            // wielder turns: the acting fingerprint flickered to the PUPPET after it acted, so a
            // wielder-keyed count ran backwards and the battle-restart carryover guard (current < last)
            // waved every re-puppet through. GlobalTurns never decreases in-battle, so the cooldown holds.
            int turnNow = _turns.GlobalTurns;
            bool canLatch = active && !_state.HasPuppet
                            && _state.CanPuppet(turnNow, Tuning.PuppeteerCooldownTurns);
            var enemyFps = canLatch ? Band.EnemyFingerprints(_mem) : null;

            for (int s = 0; s < Offsets.BandSlots; s++)
            {
                long addr = Band.Entry(s);
                if (!_mem.Readable(addr + Offsets.AMaxHp, 2)) continue;
                int mhp = _mem.U16(addr + Offsets.AMaxHp), lvl = _mem.U8(addr + Offsets.ALevel);
                if (mhp < 1 || mhp >= 2000 || lvl < 1 || lvl > 99) continue;
                int br = _mem.U8(addr + Offsets.ABrave), fa = _mem.U8(addr + Offsets.AFaith);
                if (br < 1 || br > 100 || fa < 1 || fa > 100) continue;
                int hp = _mem.Readable(addr + Offsets.AHp, 2) ? _mem.U16(addr + Offsets.AHp) : 0;

                int dmg = _hpState.Observe(s, hp);   // ALWAYS observe to keep the HP-diff baseline

                if (!canLatch || dmg <= 0 || enemyFps is null) continue;
                var fp = (mhp, lvl, br, fa);
                if (!ShouldLatch(enemyFps.Contains(fp))) continue;

                int job = _mem.Readable(addr + JobOff, 1) ? _mem.U8(addr + JobOff) : 0;
                if (!IsDominatable(job))   // allow-everyone: never trips; the re-gating hook if a job gate is reinstated
                {
                    Log.Info($"puppeteer: struck enemy ({mhp} max HP, job {job}) is gated out -- left to the AI");
                    continue;
                }

                var rawWfp = _tracker.LastActorFingerprint;
                (int lvl, int br, int fa)? wfp = rawWfp != default ? rawWfp : null;
                int wBase = wfp is { } w ? _turns.Turns(w.lvl, w.br, w.fa) : 0;
                _state.Puppet(addr, fp, turnNow, wfp, wBase, _turns.GlobalTurns);
                SetAgency(_mem, addr, true);   // hand control to the player immediately
                Log.Info($"puppeteer: DOMINATED enemy ({mhp} max HP, job {job}) -- you control it for {puppetTurns} of its turns");
                canLatch = false;              // one puppet at a time -- stop after the first
            }
        }

        Drive();
        Expire(puppetTurns);
    }

    /// <summary>Hold the agency bit SET each tick (beats the engine re-deriving it). The expiry clock
    /// now rides the WIELDER's own turn count via TurnTracker -- the puppet taking its turn no longer
    /// advances the expiry (so Drive no longer needs to observe the turn-queue hand-off).</summary>
    private void Drive()
    {
        if (!_state.HasPuppet) return;
        long addr = _state.Addr;
        var fp = _state.Fingerprint!.Value;
        if (!Valid(addr, fp)) { _state.Release(); return; }   // copy moved/freed -> drop (cooldown stays)
        SetAgency(_mem, addr, true);
    }

    /// <summary>Release when the GALEWIND WIELDER takes its next turn (its own TurnTracker clock
    /// advances past the captured baseline). If the wielder was unresolved at dominate, fall back
    /// to a GlobalTurns threshold. Either way the puppet gets a full move+act+wait: the wielder's
    /// clock only advances on the WIELDER's acted edge, never on the puppet's.</summary>
    private void Expire(int puppetTurns)
    {
        if (!_state.HasPuppet) return;
        int wTurns = _state.WielderFp is { } w ? _turns.Turns(w.lvl, w.br, w.fa) : 0;
        if (!_state.IsExpired(wTurns, _turns.GlobalTurns, puppetTurns, Tuning.PuppeteerWielderlessFallbackTurns)) return;
        Release();
        Log.Info($"puppeteer: control expired (wielder's next turn) -- the enemy reverts to AI");
    }

    /// <summary>Clear the agency bit on the active puppet (revert to AI) and drop the latch. The cooldown
    /// clock is preserved. No-op when nothing is puppeted.</summary>
    private void Release()
    {
        if (!_state.HasPuppet) return;
        long addr = _state.Addr;
        var fp = _state.Fingerprint!.Value;
        if (Valid(addr, fp)) SetAgency(_mem, addr, false);
        _state.Release();
    }

    private bool Valid(long b, (int mhp, int lvl, int br, int fa) fp)
    {
        if (!_mem.Readable(b + Offsets.AMaxHp, 2)) return false;
        return _mem.U16(b + Offsets.AMaxHp) == fp.mhp && _mem.U8(b + Offsets.ALevel) == fp.lvl
            && _mem.U8(b + Offsets.ABrave) == fp.br && _mem.U8(b + Offsets.AFaith) == fp.fa;
    }
}
