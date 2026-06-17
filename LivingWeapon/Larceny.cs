using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Arcanum's "Larceny" signature: while a +3 Arcanum is the acting wielder's main hand, each enemy
/// its action damages has its highest-priority holdable buff STOLEN -- stripped from the foe and
/// held on the wielder for LarcenyHoldTurns of the wielder's OWN turns, then dropped.
///
/// DETECTION mirrors Maim/Ricochet: per-tick HP drops on enemy band slots during the wielder's
/// acted period (enemy-fingerprint filtered). WIELDER ATTRIBUTION is per-fingerprint: each
/// Arcanum holder's (level,brave,faith) identifies it uniquely; KillTracker.LastActorFingerprint
/// (latched by TryResolveActingFingerprint once per acted-period, outside the SameSet guard) names
/// which one acted, so TWO deployed Arcanum holders are fully supported. EXPIRY counts the WIELDER'S
/// OWN completed turns via TurnTracker.Turns for its fingerprint (the proven acted-edge per-unit
/// counter). No wall-clock backstop: a deployed wielder always takes turns, and battle exit clears
/// any remainder. SAFETY: the buff is only granted+held on the wielder if it did NOT already have
/// it, so expiry never strips the wielder's own enchantment; the foe's bit is always stripped (a
/// dispel at worst). All reads/writes are VirtualQuery-guarded (LarcenyPolicy.SetBit/ClearBit).
/// </summary>
internal sealed class Larceny : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.OnField);
    private const int ArcanumId = 30;

    private readonly IGameMemory _mem;
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly KillTracker _tracker;
    private readonly TurnTracker _turns;
    private readonly LarcenyHoldings _holdings;
    private readonly RicochetState _hpState;
    private readonly LarcenyPolicy.Buff?[] _preHit;   // per-slot holdable-buff snapshot (pre-hit memory)
    private readonly List<int> _arcHand = new() { ArcanumId };
    private string _lastGateReason = "";   // gate-edge log throttle (log on change, not every 33ms tick)

    public Larceny(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, KillTracker tracker,
                   TurnTracker turns, IGameMemory? mem = null)
    {
        _mem = mem ?? new LiveMemory();
        _meta = meta;
        _kills = kills;
        _tracker = tracker;
        _turns = turns;
        _holdings = new LarcenyHoldings(_mem);
        _hpState = new RicochetState(Offsets.BandSlots);
        _preHit = new LarcenyPolicy.Buff?[Offsets.BandSlots];
    }

    /// <summary>Locate the acting wielder's live band entry by fingerprint.</summary>
    private long Locate((int lvl, int br, int fa) fp)
        => Wielder.Locate(_mem, ArcanumId, _arcHand, fp);

    /// <summary>Completed turns this battle for the wielder at this fingerprint.</summary>
    private int Turns((int lvl, int br, int fa) fp)
        => _turns.Turns(fp.lvl, fp.br, fp.fa);

    public void ResetBattle()
    {
        _holdings.ReleaseAll(Locate);
        _holdings.Clear();
        _hpState.ResetBattle();
        System.Array.Clear(_preHit, 0, _preHit.Length);
        _lastGateReason = "";
    }

    public void Tick(bool onField)
    {
        if (!onField) { _holdings.Drive(Locate); _holdings.Expire(Locate, Turns, Tuning.LarcenyHoldTurns); return; }
        if (!_meta.TryGetValue(ArcanumId, out var m) || m.Signature is null) return;
        int tier = Tuning.TierOf(_kills, ArcanumId);

        // Resolve the acting wielder by fingerprint. KillTracker.LastActorFingerprint is latched
        // once per acted-period outside the SameSet guard, so it updates even when two Arcanum
        // holders share weapon set {30} and SameSet between them is always true.
        var actorFp = _tracker.LastActorFingerprint;
        bool tierOk = LarcenyPolicy.IsActive(m.Signature, tier);
        bool actingMain = Signatures.IsActingMainHand(_tracker.LastPlayerMainHand, ArcanumId);
        int actedByte = _mem.Readable(Offsets.Acted, 1) ? _mem.U8(Offsets.Acted) : -1;
        long actingAddr = (tierOk && actingMain && actedByte == 1 && actorFp != default) ? Locate(actorFp) : 0;
        bool active = tierOk && actingMain && actedByte == 1 && actingAddr != 0;

        // Only log the gate when a +3 Arcanum is actually FIELDED this battle. A weapon that merely
        // banked kills reads tier-eligible (tier(t3)=True) and otherwise churns the gate reason every
        // turn -- spamming the log for a weapon nobody is holding. `active` already implies a deployed
        // wielder, so short-circuit past the roster scan when it is true.
        if (active || Wielder.AnyDeployedMainHand(_mem, ArcanumId))
        {
            string reason = active
                ? "ACTIVE -- the next struck foe loses a buff"
                : $"inactive [tier(t{tier})={tierOk} actingMainHand={actingMain}(lastActed mainHand id={_tracker.LastPlayerMainHand}) actedFlag={actedByte} actingWielderLocated={actingAddr != 0} actorFp=({actorFp.lvl},{actorFp.br},{actorFp.fa})]";
            if (reason != _lastGateReason)
            {
                _lastGateReason = reason;
                Log.Info($"larceny gate: {reason}");
            }
        }

        var enemyFps = active ? Band.EnemyFingerprints(_mem) : null;

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
            // Snapshot this slot's holdable buff EVERY tick; the steal uses the PRE-hit snapshot as cheap
            // insurance against any same-tick clear racing the damage read.
            var nowBuff = LarcenyPolicy.Pick(off => _mem.Readable(addr + off, 1) ? _mem.U8(addr + off) : (byte)0);
            var preHit = _preHit[s];
            _preHit[s] = nowBuff;

            if (!active || dmg <= 0 || enemyFps is null) continue;
            if (!LarcenyPolicy.ShouldLatch(enemyFps.Contains((mhp, lvl, br, fa)))) continue;

            var buff = preHit ?? nowBuff;   // the buff the foe had BEFORE the hit (the live one may be gone)
            if (buff is null) continue;     // foe carried no holdable buff
            var key = (buff.Value.Off, buff.Value.Mask);

            // One decision per struck foe -- so a single action that hits SEVERAL buffed enemies steals
            // each buff from only one of them, dispels duplicates the wielder already owns, and never
            // strips a copy it has already stolen (LarcenyPolicy.Decide).
            bool wielderHas = LarcenyPolicy.HasBit(_mem, actingAddr, key.Item1, key.Item2);
            var action = LarcenyPolicy.Decide(_holdings.IsHeld(actorFp, key), wielderHas);
            if (action == LarcenyAction.Skip) continue;   // already wearing this buff -- leave the foe's copy

            LarcenyPolicy.ClearBit(_mem, addr, key.Item1, key.Item2);   // strip the foe (Dispel + Steal)
            if (action == LarcenyAction.Steal)
            {
                _holdings.Steal(actorFp, actingAddr, key, Turns(actorFp));
                Log.Info($"larceny: STOLE {buff.Value.Name} -- held on the wielder for {Tuning.LarcenyHoldTurns} of its turns");
            }
            else
            {
                Log.Info($"larceny: DISPELLED {buff.Value.Name} from the enemy (wielder already owns it)");
            }
        }

        _holdings.Drive(Locate);
        _holdings.Expire(Locate, Turns, Tuning.LarcenyHoldTurns);
    }
}
