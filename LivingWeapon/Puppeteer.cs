using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Galewind's "Puppeteer" signature (replaces Charm-Lock): while a +3 Galewind is the acting wielder's
/// main hand, the first dominatable ENEMY its action damages is PUPPETED -- the agency flag (combat
/// +0x05 / 0x08, band -0x17) is held SET so the player controls that enemy (its move + full skillset)
/// until the WIELDER's own next turn (TurnTracker's proven per-unit acted-edge clock, Larceny's
/// mechanism), then reverts to AI.
///
/// ANTI-SNOWBALL: exactly ONE puppet at a time, and after one expires the wielder cannot dominate again
/// until Tuning.PuppeteerCooldownTurns of GLOBAL turns pass (TurnTracker.GlobalTurns -- a per-unit
/// wielder-keyed cooldown ran backwards live; see Tuning.cs). TARGET GATE (ALLOW-EVERYONE): every
/// struck enemy is dominatable (Puppeteer.IsDominatable returns true) -- the job id is not consulted
/// (it reads unreliable story/special ids on IC); the enemy + fingerprint gates below are the real
/// filter.
///
/// ARMING (D1, rebuilt 2026-07-03 -- Puppeteer had never fired live under the old latch-only gate):
/// active = onField && IsActive(tier) && (pointerPath || latchPath). pointerPath = the engine's own
/// ActorPtr naming the wielder's OWN unit BY IDENTITY (Puppeteer.Policy.PointerNamesWielder -- see
/// "D1 REVISED" below) -- the same pointer precedent TurnTracker.TryActiveViaPointer and Iai's
/// release detection both rely on. The pointer path does NOT require Acted==1: the pointer itself IS
/// the "whose turn is it" signal. latchPath = today's mechanism (LastPlayerMainHand == Galewind &&
/// Acted==1), preserved verbatim as the fallback for a benched pointer, a two-wielder ambiguity, or
/// an invalid pointer. Strictly widens the old gate; no shared latch consumer is narrowed. Pure
/// decision: see Puppeteer.Policy.WielderActing.
///
/// D1 REVISED (2026-07-04 -- plain address equality LIVE-FALSIFIED): pointerPath originally compared
/// Band.ActorEntry against Wielder.ResolveDeployedMainHand's entry by raw ADDRESS. Live log evidence
/// (2026-07-04) proved this false-negatives: a gate line logged pointerMatch=False in the exact
/// window TurnTracker attributed a turn to that SAME actor pointer -- the revolving band MIRROR seat
/// means one unit legitimately exists at multiple band addresses, so the two resolvers can each
/// return a DIFFERENT copy of the SAME unit and never compare equal by address. Fixed by comparing
/// IDENTITY via the frame nameId back-reference (Offsets.ANameId, mirroring the roster nameId
/// Offsets.RNameId -- the same mirror-safe bridge Iai's release and Wielder's tier-1 locate already
/// use): Puppeteer.Policy.PointerNamesWielder. Address equality survives as the fallback only when
/// the wielder's own roster nameId didn't resolve (unseeded/ambiguous, &lt;= 0).
///
/// RESIDUAL (accepted): ActorPtr can name the PREVIOUS actor for one tick right at the Acted rising
/// edge (observed live in the Kobu diagnosis) -- a false-positive window bounded by the one-puppet
/// latch + cooldown, never a wrong-unit dominate.
///
/// RESIDUAL (observed 2026-07-04, a victim-frame-touch pulse): the pointer transiently read a match
/// ~24ms AFTER an enemy's hit landed on a player (gate log timestamps 00:28:21.154 / 00:28:29.200
/// across the probe run), so a spurious gate-open around damage processing is possible -- the engine
/// may point ActorPtr briefly at a just-struck frame while resolving the hit. Contained, not fixed:
/// the probe observed the not-enemy verdict correctly refusing during this window, and even a
/// wrongly-opened gate stays bounded by the one-puppet latch + cooldown -- never a wrong-unit
/// dominate. Do not "fix" without a live counter-probe isolating the pulse's actual trigger.
///
/// DETECTION (D3/D4 -- Kobu's Evaluate/cache/rearm SHAPE, not Kobu's Tick skeleton, see below): per-
/// tick HP drops on enemy band slots, filtered against a per-battle ADDITIVE fingerprint cache
/// (EnemyFingerprintCache.TickField/Contains) instead of a per-latch rebuild, so a one-tick Readable()
/// flap on the static array cannot make an enemy vanish on the exact drop tick. One decision chain per
/// drop (verdict token + action together, so the diag line and the behavior cannot drift): inactive ->
/// not-enemy -> victim-dead (a kill-strike must not dominate a corpse -- a new correctness win; the
/// old code could latch a dead unit) -> holding -> cooldown -> gated-out -> rearm-unwritable (the ONE
/// detectably-transient block here; the write target is the VICTIM, so there is no "rearm-no-wielder"
/// case the way Kobu has one) -> dominated. Non-lossy: every verdict but rearm-unwritable consumes the
/// event (the RicochetState baseline moves on); the transient rearms (RicochetState.Rearm) so the SAME
/// drop re-detects next tick instead of being silently discarded, same accepted residuals as Kobu (a
/// fingerprint miss consumes even on a flapped band field; an off-field gap can pay a rearmed hit out
/// late, never to the wrong unit).
///
/// TICK SKELETON (CRITICAL, unlike Kobu): Puppeteer holds a possession across ticks, so Drive() (re-
/// assert the agency bit) and Expire() (release on the wielder's next turn) must keep running on EVERY
/// tick -- including off-field and inactive ticks -- or a held enemy either reverts to AI early (Drive
/// skipped) or never releases (Expire skipped). Kobu's early Tick returns are safe only because Kobu
/// holds nothing; the shape mirrored here is Larceny's onField-gated scan with Drive/Expire called
/// unconditionally on every path out of Tick.
///
/// EXPIRY CLOCK (D2): the wielder fingerprint captured at dominate is read DIRECTLY OFF THE WIELDER'S
/// OWN BAND ENTRY (ALevel/ABrave/AFaith), never ResolveDeployedMainHand's roster-read out param --
/// TurnTracker.Turns keys on BAND-read (level,brave,faith) (TurnTracker.TryActiveViaPointer /
/// TryActiveFingerprint), and a live band level can drift up to Band.MaxLevelDrift (9) above the
/// roster level on a mid-battle level-up. Capturing the roster fp would key the expiry clock in a
/// different address space than the one the credit path writes to -- the clock would never advance.
/// Falls back to KillTracker.LastActorFingerprint (also band-read) when the pointer path didn't
/// resolve the wielder at dominate time; falls back further to the GlobalTurns threshold when even
/// that fingerprint is unavailable (PuppeteerState.IsExpired's existing null-WFp branch).
/// KNOWN RESIDUAL (out of scope; do not fix without a live counter-probe): if the wielder's band
/// fingerprint ROTATES AFTER dominate (a mid-battle level-up mid-possession), the wielder clock keys
/// off the OLD fingerprint and never advances again -- the puppet holds until battle exit. IsExpired
/// with a non-null WFp never falls back to GlobalTurns (PuppeteerState.IsExpired, Puppeteer.Policy.cs).
///
/// ON-DECK (if live testing shows "dominated" logged but the enemy stays AI-driven): the agency bit
/// (combat +0x05/0x08, band -0x17) is single-byte-proven ONCE live (2026-06-18); the FFTMP cold-enemy
/// corroboration used the +0x1EE / band-relative +0x1D2 PAIR (LIVE_LEDGER rows 46/68 -- "a lone +0x05
/// write may revert and the pair is what holds ... not yet probed"). Do NOT add the second write
/// speculatively -- the gate-reason + per-drop diag instruments below exist to prove whether it is
/// needed before adding it.
///
/// Offsets confirmed live 2026-06-18: the agency flag (combat +0x05 / band -0x17) and the job-read
/// (JobOff == combat +0x03 / band -0x19) both read correct per-unit values in-game.
///
/// Possession-hold lifecycle (Drive/Expire/Release/Valid + the PuppetFingerprint query) lives in
/// Puppeteer.Hold.cs -- a real seam: latch-acquisition (this file) vs hold-and-release (that one).
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

    // Own-turn release edge state (LW-5, Puppeteer.Hold.cs): the puppet is held until it takes its
    // OWN turn -- the turn queue names it (Offsets.TurnQueue) across an acted rising..falling edge.
    // _pWasActed = last-tick acted flag (edge detect); _pTurnActive = a queue-named acted period is
    // open; _puppetOwnTurns = completed own-turns this possession. Reset per possession.
    private bool _pWasActed;
    private bool _pTurnActive;
    private int _puppetOwnTurns;

    // A field, not `if (Tuning.VerboseEvents)` inline: VerboseEvents is a const false in Release, so
    // the compiler proves the branch dead -- CS0162 unreachable code -- and TreatWarningsAsErrors
    // turns that into a build failure. Reading it through a readonly field (Kobu's precedent) keeps
    // the check a runtime bool instead of a compile-time constant.
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

            // D1: the pointer path is independent of the acting-latch mechanism -- see the class doc.
            // D1 REVISED 2026-07-04: identity via ANameId, not raw address -- see the class doc's
            // "D1 REVISED" paragraph and Puppeteer.Policy.PointerNamesWielder.
            long wielderEntry = Wielder.ResolveDeployedMainHand(_mem, GalewindId, out _, out int wielderNameId);
            long actorEntry = Band.ActorEntry(_mem);
            // Unguarded read (Iai/Wielder tier-1 pattern, D8): U16 fail-safes to 0 on an unreadable
            // address, and 0 never equals a resolved wielderNameId (> 0), so a bad read only ever
            // falls through to "no match", never a false positive.
            int actorNameId = actorEntry != 0 ? _mem.U16(actorEntry + Offsets.ANameId) : 0;
            bool pointerMatch = PointerNamesWielder(actorEntry, wielderEntry, actorNameId, wielderNameId);
            bool latchMainHand = Signatures.IsActingMainHand(_tracker.LastPlayerMainHand, GalewindId);
            int actedByte = _mem.U8(Offsets.Acted);
            bool active = tierOk && WielderActing(pointerMatch, latchMainHand, actedByte == 1);

            int turnNow = _turns.GlobalTurns;

            // D5: Larceny-style gate-reason instrument (Larceny.cs ~76-97) -- spam-guarded by
            // AnyDeployedMainHand (a benched/give-all reserve looks tier-eligible but isn't fielded),
            // logged only on a reason change.
            string reason = active
                ? "ACTIVE: the next struck enemy becomes your puppet"
                : $"inactive [tier(t{tier})={tierOk} pointerMatch={pointerMatch} latchMainHand={latchMainHand} actedByte={actedByte} wielderEntry={wielderEntry != 0} hasPuppet={_state.HasPuppet} offCooldown={_state.CanPuppet(turnNow, Tuning.PuppeteerCooldownTurns)}]";
            if ((active || Wielder.AnyDeployedMainHand(_mem, GalewindId)) && reason != _lastGateReason)
            {
                _lastGateReason = reason;
                ModLogger.Debug(LogVerb.Signature, $"puppeteer gate: {reason}");
            }

            // The HP-diff baseline (Observe) runs on EVERY on-field tick for every sane band slot,
            // regardless of the gate -- detection survives idle gaps (Maim's proven shape).
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
                if (dmg <= 0) continue;

                Evaluate(s, addr, dmg, hp, (mhp, lvl, br, fa), wielderEntry, active, turnNow, puppetTurns);
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

        // One decision chain: verdict token + action together so the diag line and the behavior
        // cannot drift. Rearm (baseline rollback -> next tick re-detects) fires ONLY on the one
        // detectably-transient block Puppeteer has -- an unwritable agency-byte write target; every
        // other verdict consumes. A kill-strike (victim HP now 0) always consumes: dominating a
        // corpse is the new correctness win this chain closes (today's code could latch a dead unit).
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
        // GATE-ON-ARMED (the owner-complaint line): with zero Galewinds fielded the verdict chain
        // still runs, but the per-drop diag must emit NOTHING -- not even to the file.
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
