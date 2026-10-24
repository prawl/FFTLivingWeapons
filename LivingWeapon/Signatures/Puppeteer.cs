using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Galewind's Puppeteer signature. When a +3 Galewind wielder hurts an enemy, you steer that enemy
/// for its own next turn or turns, then it goes back to the AI. The first dominatable enemy the acting
/// wielder's action damages is puppeted. The agency flag (combat +0x05 / 0x08, band -0x17) is held set
/// until the puppet has taken its own turns, with a cap in global turns (see Expire).
///
/// One puppet at a time, then a cooldown. After one expires the wielder waits
/// Tuning.PuppeteerCooldownTurns global turns (TurnTracker.GlobalTurns; a per unit cooldown ran
/// backwards live, see Tuning.cs). IsDominatable does not read the job id, which is unreliable on IC.
///
/// The mod only puppets while it believes the wielder is acting. active = onField && IsActive(tier) &&
/// ArmedNow(live), live = pointerPath || latchPath. pointerPath is ActorPtr naming the wielder's own
/// unit (Puppeteer.Policy.PointerNamesWielder), no Acted==1 needed. latchPath is the backup, the last
/// player weapon seen plus the game's acted flag (LastPlayerMainHand == Galewind && Acted == 1). See
/// WielderActing. The arm also holds for about three seconds (Puppeteer.Policy.ArmHoldTicks, 90 ticks)
/// after the live signal last read true, so a quick confirm lands when the main hand latch flickers
/// off at the strike.
///
/// Units are matched by identity, since one unit can sit at several addresses. The pointer path compares
/// the frame nameId (Offsets.ANameId, mirroring roster Offsets.RNameId) because the band mirror seat
/// false negatived address equality live. Address equality remains only when no roster nameId resolved.
///
/// Accepted residuals, each bounded by one puppet and the cooldown. ActorPtr can name the previous
/// actor for one tick at the Acted rising edge (Kobu diagnosis), and one live read matched about 24ms
/// after an enemy hit landed on a player. Inside the three second hold an enemy hurt by someone else
/// (a charged spell resolving, a poison tick) can be puppeted. Do not fix without a counter probe.
///
/// Detection spots an enemy losing HP, in Kobu's Evaluate/cache/rearm shape. Per tick HP drops on enemy
/// band slots, filtered by an additive per battle EnemyFingerprintCache so a one tick Readable flap
/// cannot drop an enemy on the drop tick. The verdict chain is inactive, not-enemy, victim-dead,
/// holding, cooldown, gated-out, rearm-unwritable, dominated. Every verdict but rearm-unwritable
/// consumes the event; that one calls RicochetState.Rearm so the same drop re-detects next tick.
/// Residuals as in Kobu. A fingerprint miss consumes even on a flapped band field, and an off field
/// gap can pay a rearmed hit out late, never to the wrong unit.
///
/// A possession outlives one tick, so Drive (re-assert the agency bit) runs on every path out of Tick and
/// Expire (release after the puppet's own turns) on every path past the meta check, off field and inactive
/// included, or a held enemy reverts early or never releases. Kobu may return early because it holds nothing.
///
/// How the release knows the puppet's turn came round. Expire watches the turn queue name the puppet
/// across an acted rising and falling edge, not a wielder clock, which released on the wrong unit's
/// turn. If that never fires, IsCapped lets go after PuppeteerWielderlessFallbackTurns global turns.
///
/// If the log says dominated but the enemy keeps acting on its own, a second write may be needed. The
/// FFTMP cold enemy corroboration wrote the +0x1EE / band +0x1D2 pair, so a lone +0x05 write may
/// not stick ([ledger agency-flag-shadow-0x1ee], not yet probed). Do not add the second write
/// speculatively; the gate reason and per drop diag exist to show whether it is needed.
///
/// The agency flag (combat +0x05 / band -0x17) and job read (JobOff, combat +0x03 / band -0x19) return
/// correct per unit values in game. The possession hold lifecycle (Drive/Expire/Release/Valid,
/// PuppetFingerprint) lives in Puppeteer.Hold.cs, split from latch acquisition here.
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
    private readonly EnemyFingerprintCache _enemies;
    private readonly Action<string, string>? _recorder;   // flight tap. Dominate + release brackets (reason=own-turn/cap); null in tests/before Flight.Init
    private const int NotArmed = int.MaxValue;
    // A quick confirm right after the strike still turns the enemy, even if the wielder signal blinked.
    // Ticks since the live signal, NotArmed when none. Feeds ArmedNow.
    private int _ticksSinceLive = NotArmed;
    internal int ArmTicks => _ticksSinceLive;
    private string _lastGateReason = "";       // gate-edge log throttle (Larceny's pattern. Log on
                                               // reason change, not every 33ms tick)

    // Own-turn release state (Puppeteer.Hold.cs). The puppet is held until the turn queue
    // (Offsets.TurnQueue) names it across an acted edge. Reset per possession.
    private bool _pWasActed;
    private bool _pTurnActive;
    private int _puppetOwnTurns;

    // A field because VerboseEvents is a const false in Release, and an inline check is CS0162
    // unreachable code, an error under TreatWarningsAsErrors (same as Kobu).
    private readonly bool _verbose = Tuning.VerboseEvents;

    public Puppeteer(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, KillTracker tracker,
                     TurnTracker turns, IGameMemory? mem = null, Action<string, string>? recorder = null)
    {
        _mem = mem ?? new LiveMemory();
        _meta = meta;
        _kills = kills;
        _tracker = tracker;
        _turns = turns;
        _recorder = recorder;
        _state = new PuppeteerState();
        _hpState = new RicochetState(Offsets.BandSlots);
        _enemies = new EnemyFingerprintCache(_mem);
    }

    public void ResetBattle()
    {
        Release();            // clear the agency bit on any active puppet (revert to AI) before clearing
        _state.Clear();
        _lastGateReason = "";
        _ticksSinceLive = NotArmed;
        _hpState.ResetBattle();
        _enemies.ResetBattle();
        ResetPossession();
    }

    public void Tick(bool onField)
    {
        if (!_meta.TryGetValue(GalewindId, out var m) || m.Signature is null) { Drive(); return; }
        int tier = Tuning.TierOf(_kills, GalewindId);
        int puppetTurns = m.Signature.PuppeteerTurns;
        bool tierOk = IsActive(m.Signature, tier);

        if (onField)
        {
            _enemies.TickField();   // additive capture; Contains below reads the battle-stable cache

            // Pointer path matches by ANameId identity, see the class doc.
            long wielderEntry = Wielder.ResolveDeployedMainHand(_mem, GalewindId, out _, out int wielderNameId);
            long actorEntry = Band.ActorEntry(_mem);
            // Unguarded read (Iai/Wielder tier-1 pattern). U16 returns 0 when unreadable and 0 never
            // equals a resolved wielderNameId (> 0), so a bad read means no match.
            int actorNameId = actorEntry != 0 ? _mem.U16(actorEntry + Offsets.ANameId) : 0;
            bool pointerMatch = PointerNamesWielder(actorEntry, wielderEntry, actorNameId, wielderNameId);
            bool latchMainHand = Signatures.IsActingMainHand(_tracker.LastPlayerMainHand, GalewindId);
            int actedByte = _mem.U8(Offsets.Acted);
            bool live = WielderActing(pointerMatch, latchMainHand, actedByte == 1);
            if (live) _ticksSinceLive = 0;
            else if (_ticksSinceLive < NotArmed) _ticksSinceLive++;
            bool active = ArmedNow(live, _ticksSinceLive, tierOk);
            bool held = active && !live;

            int turnNow = _turns.GlobalTurns;

            // Larceny-style gate-reason log (Larceny.cs ~76-97). AnyDeployedMainHand keeps a benched
            // give-all reserve from spamming; logs only when the reason changes.
            string reason = active
                ? "ACTIVE: the next struck enemy becomes your puppet"
                : $"inactive [tier(t{tier})={tierOk} pointerMatch={pointerMatch} latchMainHand={latchMainHand} actedByte={actedByte} wielderEntry={wielderEntry != 0} hasPuppet={_state.HasPuppet} offCooldown={_state.CanPuppet(turnNow, Tuning.PuppeteerCooldownTurns)}]";
            string edgeKey = held ? reason + " held" : reason;   // keeps the log to one line per change, not one per tick
            if ((active || Wielder.AnyDeployedMainHand(_mem, GalewindId)) && edgeKey != _lastGateReason)
            {
                _lastGateReason = edgeKey;
                ModLogger.Debug(LogVerb.Signature, $"puppeteer gate: {reason}{(held ? $" held={_ticksSinceLive}" : "")}");
            }

            // HP-diff baseline runs every on-field tick for every sane band slot, gate or not (as in Maim).
            for (int s = 0; s < Offsets.BandSlots; s++)
            {
                // Shared sanity read/bounds extracted to Band.TryReadUnit (Band.Sanity.cs).
                if (!Band.TryReadUnit(_mem, s, out long addr, out var fp, out int hp)) continue;

                int dmg = _hpState.Observe(s, hp);   // always observe to keep the HP-diff baseline
                if (dmg <= 0) continue;

                Evaluate(s, addr, dmg, hp, fp, wielderEntry, active, turnNow, puppetTurns);
            }
        }

        Drive();
        Expire(puppetTurns);
    }

    /// <summary>The verdict chain for one detected HP drop. Eager fail-safe reads (Kobu's
    /// pattern) so the diag line always carries the full verdict context.</summary>
    private void Evaluate(int s, long addr, int dmg, int hp, (int mhp, int lvl, int br, int fa) fp,
                         long wielderEntry, bool active, int turnNow, int puppetTurns)
    {
        bool inSet = _enemies.Contains(fp);
        bool hasPuppet = _state.HasPuppet;
        bool offCooldown = _state.CanPuppet(turnNow, Tuning.PuppeteerCooldownTurns);
        int job = _mem.Readable(addr + JobOff, 1) ? _mem.U8(addr + JobOff) : 0;
        bool dominatable = IsDominatable(job);

        // One chain, so the diag verdict and the behavior cannot drift. Only rearm-unwritable rearms
        // (baseline rollback, next tick re-detects). The write target is the victim, so there is no
        // rearm-no-wielder case the way Kobu has one. victim-dead always consumes, since the old code
        // could latch a dead unit and a kill-strike must not dominate a corpse.
        string verdict;
        bool rearm = false;
        if (!active) verdict = "inactive";
        else if (!inSet) verdict = "not-enemy";
        else if (hp == 0) verdict = "victim-dead";
        else if (hasPuppet) verdict = "holding";
        else if (!offCooldown) verdict = "cooldown";
        else if (!dominatable) verdict = "gated-out";   // allow-everyone. Never trips; the re-gating hook
        else if (!_mem.Writable(addr + AgencyOff, 1)) { verdict = "rearm-unwritable"; rearm = true; }
        else
        {
            verdict = "dominated";
            _state.Puppet(addr, fp, turnNow, _turns.GlobalTurns);
            _ticksSinceLive = NotArmed;   // spends the arm, so one confirm takes one puppet
            ResetPossession();   // the new puppet's own turn count starts from zero
            SetAgency(_mem, addr, true);   // hand control to the player immediately
            ModLogger.EventWithTrace(LogVerb.Signature,
                $"The Galewind puppets the struck enemy ({fp.mhp} maximum HP); you control it for {puppetTurns} of its turns.",
                $"puppeteer dominate detail (job {job}, battle slot {s})");
            _recorder?.Invoke("pup", $"dominate seat=0x{addr:X} nameId={(_mem.Readable(addr + Offsets.ANameId, 2) ? _mem.U16(addr + Offsets.ANameId) : 0)} mhp={fp.mhp} slot={s} job={job} gturn={_turns.GlobalTurns}");
        }
        if (rearm) _hpState.Rearm(s, hp + dmg);
        // Origin of the per-drop gate. Patrick complained about log spam with no Galewind fielded,
        // so with none fielded the diag emits nothing, not even to the file.
        if (_verbose && (wielderEntry != 0 || Wielder.AnyDeployedMainHand(_mem, GalewindId)))
            ModLogger.Debug(LogVerb.Signature, $"puppeteer evaluated: slot {s} damage {dmg} verdict={verdict} active={active} enemyInSet={inSet} hitPoints={hp} wielderLocated={wielderEntry != 0} holdingPuppet={hasPuppet} offCooldown={offCooldown} (job {job})");
    }

    /// <summary>Start a fresh possession. Clear the own-turn edge state (Puppeteer.Hold.cs's release
    /// signal). Called at every dominate and at battle reset.</summary>
    private void ResetPossession()
    {
        _pWasActed = false;
        _pTurnActive = false;
        _puppetOwnTurns = 0;
    }
}
