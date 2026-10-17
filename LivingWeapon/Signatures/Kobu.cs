using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Kiyomori's Kobu signature. When a +3 Kiyomori main hand damages an enemy whose current brave
/// (band +0x0F) exceeds the wielder's live current brave, the wielder's brave is raised to match,
/// once, capped at Tuning.KobuBraveCap. A single guarded write at strike time, with no hold. The
/// basis is the wielder's live brave read that tick, so a later dip is a fresh baseline
/// (Kobu.Policy.cs OneShotRaise).
///
/// Premise. A one-shot current-brave write on a player unit sticks across turns, and the engine does
/// not re-normalize it from orig (2026-07-02, probes/brave_oneshot_probe.py, LIVE_LEDGER row). That
/// overturned the older belief in brave_probe.py's recipe text, which is why the old per-tick hold is gone.
///
/// Offsets are band-entry-relative (band_entry = combat_base + 0x1C). Current brave is +0x0F
/// (Offsets.ABraveCurrent), read from the enemy and written once on the wielder. Orig brave is +0x0E
/// (Offsets.ABrave), the Wielder.Locate fingerprint, so it is never written. Band +0x2B is combat
/// +0x47, the Reraise/Invisible/Float status bitfield (Offsets.AReraise), not brave
/// (brave-faith-current-vs-orig-offsets).
///
/// Detection scans every on-field tick regardless of the wielder locate, as Maim does. HP-diff
/// tracking (RicochetState) baselines every tick, and enemies come from a per-battle additive
/// fingerprint cache (EnemyFingerprintCache.TickField), so a one-tick Readable() flap cannot drop an
/// enemy on the hit tick. The first tick baselines and the next tick with an HP drop detects the hit.
/// The wielder comes from Wielder.ResolveDeployedMainHand each tick, and a miss is a rearm-transient.
///
/// A detected drop is consumed, except for three transient blocks (zero brave read, unwritable target,
/// unlocatable wielder) where RicochetState.Rearm rolls the baseline back so the drop re-detects next
/// tick. The retry ends when the active gate closes. Accepted residuals are a fingerprint miss (looks
/// like an ally hit, and rearming would re-detect every ally hit) and an off-field gap, where a rearmed
/// hit can pay out late at the start of the next active window, never to the wrong unit.
/// The transient-loss failure was seen 2026-07-02, an eaten strike at 10:33:36.601 against the same
/// strike firing at 11:02:03.936 (kobu-raise-detection-diagnosis). The kobu-diag line is the
/// instrument for any residual. ResetBattle() clears the HP baselines and cached fingerprints.
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

        // A 0 here (benched or two wielders) is handled per event as a rearm. Bailing the tick
        // would hide any drop that lands during a locate gap.
        long wielderEntry = Wielder.ResolveDeployedMainHand(_mem, KiyomoriId, out _);

        int actedByte = _mem.U8(Offsets.Acted);
        int mainHand = _tracker.LastPlayerMainHand;
        bool active = Signatures.IsActingMainHand(mainHand, KiyomoriId) && actedByte == 1;

        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            // Shared sanity read/bounds extracted to Band.TryReadUnit (Band.Sanity.cs).
            if (!Band.TryReadUnit(_mem, s, out long addr, out var fp, out int hp)) continue;

            int dmg = _hpState.Observe(s, hp);
            if (dmg <= 0) continue;

            Evaluate(s, addr, dmg, hp, fp, wielderEntry, active, actedByte, mainHand);
        }
    }

    private void Evaluate(int s, long addr, int dmg, int hp, (int mhp, int lvl, int br, int fa) fp,
                          long wielderEntry, bool active, int actedByte, int mainHand)
    {
        // Reads are fail-safe so the diag line carries the full context. With wielderEntry == 0 the
        // live read is unreadable and returns 0, which the brave-read gate below catches.
        int struck = _mem.U8(addr + Offsets.ABraveCurrent);
        int live = _mem.U8(wielderEntry + Offsets.ABraveCurrent);
        bool inSet = _enemies.Contains(fp);
        int raised = OneShotRaise(live, struck, Tuning.KobuBraveCap);
        long target = wielderEntry + Offsets.ABraveCurrent;

        // Verdict and action share one chain so the diag line matches the behavior. Only the
        // transient blocks rearm; every other verdict consumes the drop.
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
            ModLogger.Event(LogVerb.Signature, $"Kiyomori struck a braver enemy (its current brave {struck}); the wielder's brave rises {live} to {raised}");
        }
        if (rearm) _hpState.Rearm(s, hp + dmg);
        // The documented transient-loss instrument (kobu-raise-detection-diagnosis): file-only, every event.
        ModLogger.Debug(LogVerb.Signature, $"kobu evaluated: slot {s} damage {dmg} verdict={verdict} actedFlag={actedByte} mainHandWeapon={mainHand} wielderLocated={wielderEntry != 0} enemyInSet={inSet} struckBrave={struck} wielderBrave={live} raisedTo={raised}");
    }
}
