using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Arcanum's "Larceny" signature: while a +3 Arcanum is the acting wielder's main hand, each enemy
/// its action damages has its highest-priority holdable buff STOLEN -- stripped from the foe and
/// held on the wielder for LarcenyTurns of the wielder's own turns, then dropped.
///
/// DETECTION mirrors Maim/Ricochet: per-tick HP drops on enemy band slots during the wielder's
/// acted period (enemy-fingerprint filtered). WIELDER LOCATION is the Rapture/SpiritualFont path
/// (Wielder.TryResolveMainHand + Locate). EXPIRY counts the WIELDER's turns off TurnTracker (the
/// player's own band CT reads flat 0 -- the Rapture wall -- so it cannot be used here). The
/// SAFETY: the buff is only granted+held on the wielder if the wielder did NOT already have it, so
/// expiry never strips the wielder's own enchantment; the foe's bit is always stripped (a dispel
/// at worst). All reads/writes are VirtualQuery-guarded (LarcenyPolicy.SetBit/ClearBit).
///
/// LIVE-PENDING: only the PROVEN-holdable buff bits are wired today (Reraise/Invisible, +0x47 --
/// the FeignDeath pair); the marquee buffs (Haste/Protect/Shell/...) are unmapped. Map them with
/// tools/probes/poison_probe.py (diff + holdbit) and add rows to LarcenyPolicy.Stealable; the
/// transfer mechanism here already works for any row.
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
    private readonly LarcenyState _state;
    private readonly RicochetState _hpState;
    private readonly List<int> _hands = new();
    private long _wielderAddr;
    private (int lvl, int br, int fa) _wielderFp;
    private bool _wasActive;

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
    }

    public void ResetBattle()
    {
        ReleaseAll();   // drop any stolen bits off the wielder before forgetting them
        _state.Clear();
        _hpState.ResetBattle();
        _wielderAddr = 0;
        _wasActive = false;
    }

    public void Tick(bool onField)
    {
        if (!onField) { Drive(); return; }   // keep holding stolen bits off the live field; NOTE expiry
                                             // runs on the on-field branch only -- it relies on the
                                             // wielder being locatable during on-field ticks (battle
                                             // exit's ReleaseAll is the backstop, so this never leaks)
        if (!_meta.TryGetValue(ArcanumId, out var m) || m.Signature is null) return;
        int tier = Tuning.TierOf(_kills, ArcanumId);

        // Locate the wielder (main-hand only) -- the unit we hold stolen buffs on.
        _wielderAddr = Wielder.TryResolveMainHand(_mem, ArcanumId, out _wielderFp, _hands)
                       ? Wielder.Locate(_mem, ArcanumId, _hands, _wielderFp) : 0;

        bool active = LarcenyPolicy.IsActive(m.Signature, tier) && _wielderAddr != 0
                      && Signatures.IsActingMainHand(_tracker.LastPlayerMainHand, ArcanumId)
                      && _mem.U8(Offsets.Acted) == 1;
        if (active != _wasActive)
        {
            _wasActive = active;
            Log.Info($"larceny {(active ? "ACTIVE -- Arcanum wielder is acting; the next struck foe loses a buff" : "inactive")}");
        }

        int larcenyTurns = m.Signature.LarcenyTurns;
        // Count the wielder's turns off the LIVE band level -- TurnTracker keys by live level, while
        // _wielderFp.lvl is the roster level that freezes for the battle; a mid-battle level-up would
        // otherwise desync the lookup and the buff would never expire. (A level-up DURING a held
        // window can still glitch the count by one tier -- the shared TurnTracker level-keying caveat
        // also affecting Wyrmblood/HoldTimedStat; the buff then holds to battle exit where ReleaseAll
        // clears it, never a leak.)
        int wielderLvl = _wielderAddr != 0 ? _mem.U8(_wielderAddr + Offsets.ALevel) : _wielderFp.lvl;
        int wielderTurns = _turns.Turns(wielderLvl, _wielderFp.br, _wielderFp.fa);
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
            if (!active || dmg <= 0 || enemyFps is null) continue;
            if (!LarcenyPolicy.ShouldLatch(enemyFps.Contains((mhp, lvl, br, fa)))) continue;

            var buff = LarcenyPolicy.Pick(off => _mem.Readable(addr + off, 1) ? _mem.U8(addr + off) : (byte)0);
            if (buff is null) continue;
            var key = (buff.Value.Off, buff.Value.Mask);
            if (_state.IsHeld(key)) continue;   // already wearing this buff -- leave the foe's for a later steal

            // Always strip it from the foe; grant+hold on the wielder only if they lack it (so the
            // expiry clear never strips the wielder's OWN enchantment).
            // LIMITATION: if the wielder LATER gains this same buff from another source while the
            // stolen copy is still held, expiry's bit-clear strips that one too -- bit-keyed status
            // can't tell the copies apart. Low frequency; accepted.
            LarcenyPolicy.ClearBit(_mem, addr, key.Item1, key.Item2);
            if (!LarcenyPolicy.HasBit(_mem, _wielderAddr, key.Item1, key.Item2))
            {
                LarcenyPolicy.SetBit(_mem, _wielderAddr, key.Item1, key.Item2);
                _state.Steal(key, wielderTurns);
                Log.Info($"larceny: stole {buff.Value.Name} from an enemy ({mhp} max HP) -- the wielder wears it for {larcenyTurns} of its turns");
            }
            else
            {
                Log.Info($"larceny: dispelled {buff.Value.Name} from an enemy ({mhp} max HP) -- the wielder already had it");
            }
        }

        Drive();
        ExpireAll(larcenyTurns, wielderTurns);
    }

    /// <summary>Re-assert every held stolen bit on the wielder (beats the engine's per-turn
    /// normalize), provided the located address still belongs to the same wielder.</summary>
    private void Drive()
    {
        if (!SameWielder()) return;
        foreach (var (off, mask) in _state.Held)
            LarcenyPolicy.SetBit(_mem, _wielderAddr, off, mask);
    }

    /// <summary>Drop stolen buffs whose term has elapsed (counted in the wielder's own turns).</summary>
    private void ExpireAll(int larcenyTurns, int wielderTurns)
    {
        if (_wielderAddr == 0) return;
        List<(int off, byte mask)>? drop = null;
        foreach (var key in _state.Held)
            if (LarcenyPolicy.IsExpired(wielderTurns, _state.BaselineTurns(key), larcenyTurns))
                (drop ??= new()).Add(key);
        if (drop is null) return;
        foreach (var key in drop)
        {
            if (SameWielder()) LarcenyPolicy.ClearBit(_mem, _wielderAddr, key.off, key.mask);
            _state.Release(key);
            Log.Info($"larceny: a stolen buff faded from the wielder after {larcenyTurns} turns");
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
