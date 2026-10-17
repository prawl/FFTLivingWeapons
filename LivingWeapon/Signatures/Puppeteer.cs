using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Galewind's "Puppeteer" signature (replaces Charm-Lock). While a +3 Galewind is the acting wielder's
/// main hand, the first dominatable enemy its action damages is puppeted. The agency flag (combat
/// +0x05 / 0x08, band -0x17) is held set so the player controls that enemy until the wielder's own
/// next turn (TurnTracker's per-unit acted-edge clock, as in Larceny), then it reverts to AI.
///
/// One puppet at a time. After one expires the wielder waits Tuning.PuppeteerCooldownTurns GLOBAL
/// turns (TurnTracker.GlobalTurns; a per-unit cooldown ran backwards live, see Tuning.cs). Every
/// struck enemy is dominatable: IsDominatable does not read the job id, which is unreliable on IC.
///
/// Arming (D1, rebuilt 2026-07-03 after Puppeteer never fired under the old latch-only gate):
/// active = onField && IsActive(tier) && (pointerPath || latchPath). pointerPath is ActorPtr naming
/// the wielder's own unit (Puppeteer.Policy.PointerNamesWielder), no Acted==1 needed. latchPath is
/// the old LastPlayerMainHand == Galewind && Acted==1 test, kept as fallback. See WielderActing.
///
/// D1 REVISED (2026-07-04): comparing Band.ActorEntry to the resolved main-hand entry by address
/// false-negatived in the log (pointerMatch=False while TurnTracker attributed a turn to that actor),
/// because the band mirror seat puts one unit at several addresses. It now compares the frame nameId
/// (Offsets.ANameId, mirroring roster Offsets.RNameId, as Iai's release and Wielder's tier-1 locate
/// do). Address equality remains only when the roster nameId did not resolve (&lt;= 0).
///
/// Accepted residuals, each bounded by the one-puppet latch and cooldown, never a wrong-unit dominate.
/// ActorPtr can name the previous actor for one tick at the Acted rising edge (Kobu diagnosis). On
/// 2026-07-04 the pointer read a match ~24ms after an enemy hit landed on a player (gate log
/// 00:28:21.154 / 00:28:29.200), possibly a just-struck frame. Do not fix without a counter-probe.
///
/// Detection (D3/D4) uses Kobu's Evaluate/cache/rearm shape: per-tick HP drops on enemy band slots,
/// filtered by an additive per-battle EnemyFingerprintCache so a one-tick Readable() flap cannot drop
/// an enemy on the drop tick. The verdict chain is inactive, not-enemy, victim-dead, holding,
/// cooldown, gated-out, rearm-unwritable, dominated. Every verdict but rearm-unwritable consumes the
/// event; that one calls RicochetState.Rearm so the same drop re-detects next tick. Residuals as in
/// Kobu: a fingerprint miss consumes even on a flapped band field, and an off-field gap can pay a
/// rearmed hit out late, never to the wrong unit.
///
/// Tick skeleton differs from Kobu. A possession spans ticks, so Drive() (re-assert the agency bit)
/// and Expire() (release on the wielder's next turn) run on every path out of Tick, off-field
/// and inactive included, or a held enemy reverts early or never releases. Kobu may return early
/// because it holds nothing.
///
/// Expiry clock (D2): the wielder fingerprint at dominate comes from the wielder's own band entry
/// (ALevel/ABrave/AFaith), not ResolveDeployedMainHand's roster read. TurnTracker.Turns keys on
/// band-read values (TryActiveViaPointer / TryActiveFingerprint) and band level can run up to
/// Band.MaxLevelDrift (9) above roster level after a mid-battle level-up, so a roster fingerprint
/// would never advance the clock. Falls back to KillTracker.LastActorFingerprint, then to the
/// GlobalTurns threshold if that is unavailable (PuppeteerState.IsExpired, null WFp branch).
/// Known residual: if the band fingerprint rotates mid-possession (level-up), the clock never
/// advances and the puppet holds until battle exit. IsExpired with a non-null WFp does not fall
/// back to GlobalTurns (Puppeteer.Policy.cs). Do not fix without a counter-probe.
///
/// On-deck if "dominated" logs but the enemy stays AI-driven: the agency byte write was confirmed
/// once on 2026-06-18, but the FFTMP cold-enemy corroboration used the +0x1EE / band +0x1D2 pair
/// (LIVE_LEDGER rows 46/68, "a lone +0x05 write may revert and the pair is what holds ... not yet
/// probed"). Do not add the second write speculatively; the gate-reason and per-drop diag exist to
/// show whether it is needed.
///
/// Offsets confirmed 2026-06-18: agency flag (combat +0x05 / band -0x17) and job read (JobOff,
/// combat +0x03 / band -0x19) return correct per-unit values in-game.
///
/// Possession-hold lifecycle (Drive/Expire/Release/Valid, PuppetFingerprint) lives in
/// Puppeteer.Hold.cs, split from latch acquisition here.
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
    private readonly Action<string, string>? _recorder;   // flight tap: dominate + release brackets (reason=own-turn/cap); null in tests/before Flight.Init
    private string _lastGateReason = "";       // gate-edge log throttle (Larceny's pattern: log on
                                               // reason change, not every 33ms tick)

    // Own-turn release state (LW-5, Puppeteer.Hold.cs). The puppet is held until the turn queue
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

            // D1 / D1 REVISED 2026-07-04. Pointer path matches by ANameId identity, see the class doc.
            long wielderEntry = Wielder.ResolveDeployedMainHand(_mem, GalewindId, out _, out int wielderNameId);
            long actorEntry = Band.ActorEntry(_mem);
            // Unguarded read (Iai/Wielder tier-1 pattern, D8). U16 returns 0 when unreadable and 0 never
            // equals a resolved wielderNameId (> 0), so a bad read means no match.
            int actorNameId = actorEntry != 0 ? _mem.U16(actorEntry + Offsets.ANameId) : 0;
            bool pointerMatch = PointerNamesWielder(actorEntry, wielderEntry, actorNameId, wielderNameId);
            bool latchMainHand = Signatures.IsActingMainHand(_tracker.LastPlayerMainHand, GalewindId);
            int actedByte = _mem.U8(Offsets.Acted);
            bool active = tierOk && WielderActing(pointerMatch, latchMainHand, actedByte == 1);

            int turnNow = _turns.GlobalTurns;

            // D5: Larceny-style gate-reason log (Larceny.cs ~76-97). AnyDeployedMainHand keeps a benched
            // give-all reserve from spamming; logs only when the reason changes.
            string reason = active
                ? "ACTIVE: the next struck enemy becomes your puppet"
                : $"inactive [tier(t{tier})={tierOk} pointerMatch={pointerMatch} latchMainHand={latchMainHand} actedByte={actedByte} wielderEntry={wielderEntry != 0} hasPuppet={_state.HasPuppet} offCooldown={_state.CanPuppet(turnNow, Tuning.PuppeteerCooldownTurns)}]";
            if ((active || Wielder.AnyDeployedMainHand(_mem, GalewindId)) && reason != _lastGateReason)
            {
                _lastGateReason = reason;
                ModLogger.Debug(LogVerb.Signature, $"puppeteer gate: {reason}");
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

    /// <summary>The verdict chain for one detected HP drop (D3). Eager fail-safe reads (Kobu's
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
        else if (!dominatable) verdict = "gated-out";   // allow-everyone: never trips; the re-gating hook
        else if (!_mem.Writable(addr + AgencyOff, 1)) { verdict = "rearm-unwritable"; rearm = true; }
        else
        {
            verdict = "dominated";
            _state.Puppet(addr, fp, turnNow, _turns.GlobalTurns);
            ResetPossession();   // fresh own-turn edge state + recon span (t=0)
            SetAgency(_mem, addr, true);   // hand control to the player immediately
            ModLogger.EventWithTrace(LogVerb.Signature,
                $"The Galewind puppets the struck enemy ({fp.mhp} maximum HP); you control it for {puppetTurns} of its turns.",
                $"puppeteer dominate detail (job {job}, battle slot {s})");
            _recorder?.Invoke("pup", $"dominate seat=0x{addr:X} nameId={(_mem.Readable(addr + Offsets.ANameId, 2) ? _mem.U16(addr + Offsets.ANameId) : 0)} mhp={fp.mhp} slot={s} job={job} gturn={_turns.GlobalTurns}");
        }
        if (rearm) _hpState.Rearm(s, hp + dmg);
        // Origin of the per-drop gate: Patrick complained about log spam with no Galewind fielded,
        // so with none fielded the diag emits nothing, not even to the file.
        if (_verbose && (wielderEntry != 0 || Wielder.AnyDeployedMainHand(_mem, GalewindId)))
            ModLogger.Debug(LogVerb.Signature, $"puppeteer evaluated: slot {s} damage {dmg} verdict={verdict} active={active} enemyInSet={inSet} hitPoints={hp} wielderLocated={wielderEntry != 0} holdingPuppet={hasPuppet} offCooldown={offCooldown} (job {job})");
    }

    /// <summary>Start a fresh possession: clear the own-turn edge state (Puppeteer.Hold.cs's release
    /// signal). Called at every dominate and at battle reset.</summary>
    private void ResetPossession()
    {
        _pWasActed = false;
        _pTurnActive = false;
        _puppetOwnTurns = 0;
    }
}
