using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Kiyomori's "Kobu" signature: while a +3 Kiyomori is wielded as the main hand, whenever
/// the wielder's action damages an enemy whose CURRENT brave (band +0x0F) exceeds the
/// wielder's own LIVE current brave, the wielder's current brave is raised to match, ONE SHOT
/// (capped at Tuning.KobuBraveCap). Katana formula 1: PA x Brave/100 x WP -- striking a braver
/// foe sharpens the blade in the moment.
///
/// SEMANTICS: a single guarded write at strike-detection time, nothing more. No ceiling state,
/// no hold, no re-assertion -- between strikes the wielder's brave is a normal stat, free to
/// fall (a Brave Break etc. sticks). The comparison basis for each qualifying hit is the
/// wielder's LIVE current brave read fresh that tick, not any remembered high-water mark: a dip
/// below a prior raise is a legitimate new baseline for the next comparison (Kobu.Policy.cs:
/// OneShotRaise).
///
/// PREMISE: a one-shot current-brave write on a PLAYER unit STICKS across turns, including the
/// unit's own actions -- the engine does NOT re-normalize it from orig. Live-verified
/// 2026-07-02, probes/brave_oneshot_probe.py (a one-shot write held across several rounds);
/// LIVE_LEDGER row. This CONTRADICTS the older belief (still noted in brave_probe.py's older
/// recipe text) that a current-only one-shot gets re-normalized back up from orig on the unit's
/// next turn -- that belief is why the old code held every tick; the hold is gone.
///
/// ADDRESSING -- all reads/writes are BAND-ENTRY-RELATIVE (band_entry = combat_base + 0x1C):
///   Current brave = band +0x0F (Offsets.ABraveCurrent). Read from the enemy; written once on the wielder.
///   Orig brave    = band +0x0E (Offsets.ABrave). NEVER write: it re-normalizes, never displays,
///                   and is the Wielder.Locate fingerprint -- writing it would break the next locate.
///   NEVER use +0x2B off a band address: band_entry+0x2B = combat+0x47 = the Reraise/Invisible/Float
///   STATUS bitfield (Offsets.AReraise), not brave. Proven layout: brave-faith-current-vs-orig-offsets.
///
/// DETECTION: scans every on-field tick regardless of the wielder locate (Maim's shape) -- HP-diff
///   tracking (RicochetState) baselines every on-field tick, and the enemy filter reads a per-battle
///   ADDITIVE fingerprint cache (EnemyFingerprintCache.TickField) instead of a per-tick rebuild, so a
///   one-tick Readable() flap on the static array cannot make an enemy vanish on the exact drop tick.
///   One module per hit: the first tick baselines silently, the next tick with a HP drop detects the
///   hit -- same two-tick window as Maim/Ricochet.
/// WIELDER LOCATE: Wielder.ResolveDeployedMainHand each tick (single deployed main-hand wielder); a
///   miss (0) no longer bails the whole tick -- it is handled per-event as a rearm-transient (below).
/// NON-LOSSY CONSUMPTION: a detected HP drop's event is consumed (baseline moves on) EXCEPT for three
///   DETECTABLY-transient blocks -- a fail-safe-zero brave read, an unwritable write target, or an
///   unlocatable wielder -- where RicochetState.Rearm rolls the baseline back so the SAME drop
///   re-detects next tick, instead of the old code's silent, permanent discard. The retry is naturally
///   bounded: the first tick the active gate is closed consumes the rearmed delta. Two accepted
///   residuals: (1) a fingerprint miss consumes even on a flapped band field -- indistinguishable from
///   a genuine ally/guest hit, and rearming those would re-detect every ally hit for the whole active
///   window; (2) an off-field gap skips the scan entirely, so a rearmed genuine hit can survive it and
///   pay out at the START of the wielder's next active window -- late, but never to the wrong unit.
/// PROVENANCE: the transient-loss failure mode was proven live 2026-07-02 -- an eaten strike at
///   10:33:36.601 with the active gate open, vs the identical strike firing cleanly at 11:02:03.936
///   (memory kobu-raise-detection-diagnosis); the kobu-diag line below is the instrument for any
///   residual class still uncaught.
/// RESET: ResetBattle() clears the HP baselines (RicochetState) AND the cached enemy fingerprints
///   (EnemyFingerprintCache); there is no ceiling to clear.
/// All reads/writes are VirtualQuery-guarded.
/// </summary>
internal sealed partial class Kobu : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.OnField);
    private const int KiyomoriId = 43;

    private readonly IGameMemory _mem;
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly KillTracker _tracker;
    private readonly RicochetState _hpState;   // HP-diff per-slot tracking (same pattern as Maim)
    private readonly EnemyFingerprintCache _enemies;

    public Kobu(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, KillTracker tracker,
                IGameMemory? mem = null)
    {
        _mem = mem ?? new LiveMemory();
        _meta = meta;
        _kills = kills;
        _tracker = tracker;
        _hpState = new RicochetState(Offsets.BandSlots);
        _enemies = new EnemyFingerprintCache(_mem);
    }

    public void ResetBattle()
    {
        _hpState.ResetBattle();
        _enemies.ResetBattle();
    }

    public void Tick(bool onField)
    {
        if (!_meta.TryGetValue(KiyomoriId, out var m) || m.Signature is null) return;
        if (!IsActive(m.Signature, Tuning.TierOf(_kills, KiyomoriId))) return;
        if (!onField) return;

        _enemies.TickField();   // additive capture; the Contains below reads the battle-stable cache

        // Locate the single deployed main-hand wielder. 0 (benched / ambiguous two-wielder) is
        // handled per-event below as a rearm-transient, NOT a whole-tick bail: the scan must keep
        // observing baselines regardless (Maim's proven shape) or a drop during a locate gap
        // becomes invisible forever.
        long wielderEntry = Wielder.ResolveDeployedMainHand(_mem, KiyomoriId, out _);

        int actedByte = _mem.U8(Offsets.Acted);
        int mainHand = _tracker.LastPlayerMainHand;
        bool active = Signatures.IsActingMainHand(mainHand, KiyomoriId) && actedByte == 1;

        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Band.Entry(s);
            if (!_mem.Readable(addr + Offsets.AMaxHp, 2)) continue;
            int mhp = _mem.U16(addr + Offsets.AMaxHp), lvl = _mem.U8(addr + Offsets.ALevel);
            if (mhp < 1 || mhp >= 2000 || lvl < 1 || lvl > 99) continue;
            int br = _mem.U8(addr + Offsets.ABrave), fa = _mem.U8(addr + Offsets.AFaith);
            if (br < 1 || br > 100 || fa < 1 || fa > 100) continue;
            int hp = _mem.Readable(addr + Offsets.AHp, 2) ? _mem.U16(addr + Offsets.AHp) : 0;

            int dmg = _hpState.Observe(s, hp);
            if (dmg <= 0) continue;

            Evaluate(s, addr, dmg, hp, (mhp, lvl, br, fa), wielderEntry, active, actedByte, mainHand);
        }
    }

    private void Evaluate(int s, long addr, int dmg, int hp, (int mhp, int lvl, int br, int fa) fp,
                          long wielderEntry, bool active, int actedByte, int mainHand)
    {
        // Eager fail-safe reads so the diagnostic line always carries the full verdict context.
        // wielderEntry == 0 makes the live read U8(0 + ABraveCurrent) -- unreadable in production,
        // unseeded in the fake; both fail-safe to 0, which the insane-read gate below catches.
        int struck = _mem.U8(addr + Offsets.ABraveCurrent);
        int live = _mem.U8(wielderEntry + Offsets.ABraveCurrent);
        bool inSet = _enemies.Contains(fp);
        int raised = OneShotRaise(live, struck, Tuning.KobuBraveCap);
        long target = wielderEntry + Offsets.ABraveCurrent;

        // One decision chain: verdict token + action together so the diag line and the behavior
        // cannot drift. Rearm (baseline rollback -> next tick re-detects) fires ONLY on
        // detectably-transient blocks; every legit verdict consumes. The retry loop is naturally
        // bounded: the first tick with active == false consumes the rearmed delta. ACCEPTED
        // RESIDUALS: (1) a fingerprint miss consumes even if a band field flapped on the drop
        // tick -- indistinguishable from a genuine ally/guest hit, and rearming those would
        // re-detect every ally hit for the whole active window; (2) an off-field gap skips the
        // scan entirely, so a rearmed genuine hit can survive it and pay out at the START of the
        // wielder's next active window -- late, but never to the wrong unit.
        string verdict;
        bool rearm = false;
        if (!active) verdict = "inactive";
        else if (!inSet) verdict = "not-enemy";
        else if (wielderEntry == 0) { verdict = "rearm-no-wielder"; rearm = true; }
        else if (struck < 1 || struck > 100 || live < 1 || live > 100) { verdict = "rearm-brave-read"; rearm = true; }
        else if (raised <= 0) verdict = "no-op";
        else if (!_mem.Writable(target, 1)) { verdict = "rearm-unwritable"; rearm = true; }
        else
        {
            verdict = "raised";
            _mem.W8(target, (byte)raised);
            ModLogger.Log($"kobu: struck enemy (current brave {struck}) -- wielder brave raised {live} -> {raised}");
        }
        if (rearm) _hpState.Rearm(s, hp + dmg);
        ModLogger.LogDebug($"kobu-diag: slot {s} dmg {dmg} verdict={verdict} acted={actedByte} mainHand={mainHand} wielder={wielderEntry != 0} inSet={inSet} struck={struck} live={live} raised={raised}");
    }
}
