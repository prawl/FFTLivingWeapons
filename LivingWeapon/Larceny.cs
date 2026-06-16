using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Arcanum's "Larceny" signature: while a +3 Arcanum is the acting wielder's main hand, each enemy
/// its action damages has its highest-priority holdable buff STOLEN -- stripped from the foe and
/// held on the wielder for LarcenyHoldTurns of the wielder's OWN turns, then dropped.
///
/// DETECTION mirrors Maim/Ricochet: per-tick HP drops on enemy band slots during the wielder's
/// acted period (enemy-fingerprint filtered). WIELDER LOCATION is the single DEPLOYED Arcanum
/// main-hand holder (Wielder.ResolveDeployedMainHand -- a benched reserve no longer blocks it).
/// EXPIRY counts the WIELDER'S OWN completed turns via TurnTracker.Turns for its fingerprint (the
/// proven acted-edge per-unit counter); the GLOBAL-turn clock that preceded it did not expire the buff
/// in a normal fight (2026-06-16). No wall-clock backstop: a deployed wielder always takes turns (you
/// can't bench mid-battle), and battle exit clears any remainder.
/// SAFETY: the buff is only granted+held on the wielder if it did NOT already have it, so expiry never
/// strips the wielder's own enchantment; the foe's bit is always stripped (a dispel at worst). All
/// reads/writes are VirtualQuery-guarded (LarcenyPolicy.SetBit/ClearBit).
/// </summary>
internal sealed class Larceny : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.OnField);
    private const int ArcanumId = 30;

    private readonly IGameMemory _mem;
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly KillTracker _tracker;
    private readonly TurnTracker _turns;              // the per-unit turn counter the stolen-buff expiry rides
    private readonly LarcenyState _state;
    private readonly RicochetState _hpState;
    private readonly LarcenyPolicy.Buff?[] _preHit;   // per-slot holdable-buff snapshot (pre-hit memory)
    private long _wielderAddr;
    private (int lvl, int br, int fa) _wielderFp;
    private string _lastGateReason = "";   // gate-edge log throttle (log on change, not every 33ms tick)

    public Larceny(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, KillTracker tracker,
                   TurnTracker turns, IGameMemory? mem = null)
    {
        _mem = mem ?? new LiveMemory();
        _meta = meta;
        _kills = kills;
        _tracker = tracker;
        _turns = turns;
        _state = new LarcenyState();
        _hpState = new RicochetState(Offsets.BandSlots);
        _preHit = new LarcenyPolicy.Buff?[Offsets.BandSlots];
    }

    /// <summary>The wielder's completed turns this battle (the expiry clock) -- TurnTracker's proven
    /// per-unit acted-edge count for the located wielder's fingerprint. 0 when not located.</summary>
    private int WielderTurns() => _turns.Turns(_wielderFp.lvl, _wielderFp.br, _wielderFp.fa);

    public void ResetBattle()
    {
        ReleaseAll();   // drop any stolen bits off the wielder before forgetting them
        _state.Clear();
        _hpState.ResetBattle();
        System.Array.Clear(_preHit, 0, _preHit.Length);
        _wielderAddr = 0;
        _lastGateReason = "";
    }

    public void Tick(bool onField)
    {
        if (!onField) { Drive(); ExpireAll(); return; }   // hold + age stolen buffs off the live field too
        if (!_meta.TryGetValue(ArcanumId, out var m) || m.Signature is null) return;
        int tier = Tuning.TierOf(_kills, ArcanumId);

        // Locate the wielder: the single DEPLOYED Arcanum main-hand holder. NOT "the one roster wielder"
        // -- a benched reserve (or an enemy the dev give-all armed) must not create false ambiguity.
        _wielderAddr = Wielder.ResolveDeployedMainHand(_mem, ArcanumId, out _wielderFp);

        // The active gate = tier earned AND wielder located AND the acting player wields Arcanum in the
        // main hand AND the acted flag is set. Log the FULL breakdown on change, so a stuck signature
        // reveals WHICH condition blocks it.
        bool tierOk = LarcenyPolicy.IsActive(m.Signature, tier);
        bool actingMain = Signatures.IsActingMainHand(_tracker.LastPlayerMainHand, ArcanumId);
        int actedByte = _mem.Readable(Offsets.Acted, 1) ? _mem.U8(Offsets.Acted) : -1;
        bool active = tierOk && _wielderAddr != 0 && actingMain && actedByte == 1;
        string reason = active
            ? "ACTIVE -- the next struck foe loses a buff"
            : $"inactive [tier(t{tier})={tierOk} wielderLocated={_wielderAddr != 0} actingMainHand={actingMain}(lastActed mainHand id={_tracker.LastPlayerMainHand}) actedFlag={actedByte}]";
        if (reason != _lastGateReason)
        {
            _lastGateReason = reason;
            Log.Info($"larceny gate: {reason}");
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
            bool wielderHas = LarcenyPolicy.HasBit(_mem, _wielderAddr, key.Item1, key.Item2);
            var action = LarcenyPolicy.Decide(_state.IsHeld(key), wielderHas);
            if (action == LarcenyAction.Skip) continue;   // already wearing this buff -- leave the foe's copy

            LarcenyPolicy.ClearBit(_mem, addr, key.Item1, key.Item2);   // strip the foe (Dispel + Steal)
            if (action == LarcenyAction.Steal)
            {
                LarcenyPolicy.SetBit(_mem, _wielderAddr, key.Item1, key.Item2);
                _state.Steal(key, WielderTurns());
                Log.Info($"larceny: STOLE {buff.Value.Name} -- held on the wielder for {Tuning.LarcenyHoldTurns} of its turns");
            }
            else
            {
                Log.Info($"larceny: DISPELLED {buff.Value.Name} from the enemy (wielder already owns it)");
            }
        }

        Drive();
        ExpireAll();
    }

    /// <summary>Re-assert every held stolen bit on the wielder (beats the engine's per-turn
    /// normalize), provided the located address still belongs to the same wielder.</summary>
    private void Drive()
    {
        if (!SameWielder()) return;
        foreach (var (off, mask) in _state.Held)
            LarcenyPolicy.SetBit(_mem, _wielderAddr, off, mask);
    }

    /// <summary>Drop stolen buffs whose term has elapsed -- LarcenyHoldTurns of the wielder's own
    /// completed turns since the steal. Gated on SameWielder so a buff is only released once we can
    /// actually clear its bit -- if the wielder isn't locatable this tick we retry next tick (and battle
    /// exit's ReleaseAll is the backstop), so a faded buff never lingers.</summary>
    private void ExpireAll()
    {
        if (!SameWielder()) return;
        int turn = WielderTurns();
        List<(int off, byte mask)>? drop = null;
        foreach (var key in _state.Held)
            if (LarcenyPolicy.IsExpired(turn, _state.StolenAt(key), Tuning.LarcenyHoldTurns))
                (drop ??= new()).Add(key);
        if (drop is null) return;
        foreach (var key in drop)
        {
            LarcenyPolicy.ClearBit(_mem, _wielderAddr, key.off, key.mask);
            _state.Release(key);
            Log.Info($"larceny: a stolen buff FADED from the wielder (after {Tuning.LarcenyHoldTurns} of its turns)");
        }
    }

    /// <summary>Drop every held bit off the wielder (battle exit).</summary>
    private void ReleaseAll()
    {
        if (!SameWielder()) return;   // wielder gone/migrated -> the fresh per-battle struct clears it
        foreach (var (off, mask) in _state.Held)
            LarcenyPolicy.ClearBit(_mem, _wielderAddr, off, mask);
    }

    /// <summary>The located address still carries the wielder's (level,brave,faith) fingerprint --
    /// so a migrated/freed entry is never written (the Rapture/Maim same-unit discipline).</summary>
    private bool SameWielder()
    {
        // Tolerate upward level drift (Band.LevelMatchesRoster -- the same window Wielder.Locate
        // accepts): an EXACT-level check would reject the wielder the instant it levels up mid-battle,
        // silently dropping the hold (Drive stops re-asserting, and the bit is never cleanly released).
        if (_wielderAddr == 0 || !_mem.Readable(_wielderAddr + Offsets.AFaith, 1)) return false;
        return Band.LevelMatchesRoster(_wielderFp.lvl, _mem.U8(_wielderAddr + Offsets.ALevel))
            && _mem.U8(_wielderAddr + Offsets.ABrave) == _wielderFp.br
            && _mem.U8(_wielderAddr + Offsets.AFaith) == _wielderFp.fa;
    }
}
