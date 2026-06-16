using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Arcanum's "Larceny" signature: while a +3 Arcanum is the acting wielder's main hand, each enemy
/// its action damages has its highest-priority holdable buff STOLEN -- stripped from the foe and
/// held on the wielder for LarcenyTurns of the wielder's own turns, then dropped.
///
/// DETECTION mirrors Maim/Ricochet: per-tick HP drops on enemy band slots during the wielder's
/// acted period (enemy-fingerprint filtered). WIELDER LOCATION is the Rapture/SpiritualFont path
/// (Wielder.TryResolveMainHand + Locate). EXPIRY counts GLOBAL turns -- any unit's turn, off
/// TurnTracker.GlobalTurns -- because counting the WIELDER's own turns let a parked unit hold the
/// buff forever (it never takes a turn; observed live 2026-06-14, held through 6 sat-out turns) and
/// wall-clock bled down in menus and ignored battle pace. The world's turn clock keeps ticking no
/// matter what the wielder does, so the theft is always temporary (Tuning.LarcenyHoldTurns turns). The
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
    private readonly TurnTracker _turns;              // the global-turn clock the stolen-buff expiry rides
    private readonly LarcenyState _state;
    private readonly RicochetState _hpState;
    private readonly LarcenyPolicy.Buff?[] _preHit;   // per-slot holdable-buff snapshot (pre-hit memory)
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
        _preHit = new LarcenyPolicy.Buff?[Offsets.BandSlots];
    }

    public void ResetBattle()
    {
        ReleaseAll();   // drop any stolen bits off the wielder before forgetting them
        _state.Clear();
        _hpState.ResetBattle();
        System.Array.Clear(_preHit, 0, _preHit.Length);
        _wielderAddr = 0;
        _wasActive = false;
    }

    public void Tick(bool onField)
    {
        if (!onField) { Drive(); ExpireAll(); return; }   // hold + age stolen buffs off the live field too
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
            // Snapshot this slot's holdable buff EVERY tick. The steal uses the PRE-hit snapshot:
            // Arcanum's base strip-proc (onHit 55) clears the buff during the hit, a beat before our
            // post-hit scan, so reading it at damage time finds nothing (proven live 2026-06-14).
            var nowBuff = LarcenyPolicy.Pick(off => _mem.Readable(addr + off, 1) ? _mem.U8(addr + off) : (byte)0);
            var preHit = _preHit[s];
            _preHit[s] = nowBuff;

            if (!active || dmg <= 0 || enemyFps is null) continue;
            if (!LarcenyPolicy.ShouldLatch(enemyFps.Contains((mhp, lvl, br, fa)))) continue;

            var buff = preHit ?? nowBuff;   // the buff the foe had BEFORE the hit (the live one may be gone)
            if (buff is null) continue;
            var key = (buff.Value.Off, buff.Value.Mask);

            // One decision per struck foe -- so a single action that hits SEVERAL buffed enemies steals
            // each buff from only one of them, dispels duplicates the wielder already owns, and never
            // strips a copy it has already stolen (LarcenyPolicy.Decide).
            // LIMITATION: if the wielder LATER gains this same buff from another source while the stolen
            // copy is still held, expiry's bit-clear strips that one too -- bit-keyed status can't tell
            // the copies apart. Low frequency; accepted.
            var action = LarcenyPolicy.Decide(_state.IsHeld(key),
                                              LarcenyPolicy.HasBit(_mem, _wielderAddr, key.Item1, key.Item2));
            if (action == LarcenyAction.Skip) continue;   // already wearing this buff -- leave the foe's copy

            LarcenyPolicy.ClearBit(_mem, addr, key.Item1, key.Item2);   // strip the foe (Dispel + Steal)
            if (action == LarcenyAction.Steal)
            {
                LarcenyPolicy.SetBit(_mem, _wielderAddr, key.Item1, key.Item2);
                _state.Steal(key, _turns.GlobalTurns);
                Log.Info($"larceny: stole {buff.Value.Name} from an enemy ({mhp} max HP) -- the wielder wears it for ~{Tuning.LarcenyHoldTurns} turns");
            }
            else
            {
                Log.Info($"larceny: dispelled {buff.Value.Name} from an enemy ({mhp} max HP) -- the wielder already had it");
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

    /// <summary>Drop stolen buffs whose global-turn term has elapsed. Gated on SameWielder so a buff is
    /// only released once we can actually clear its bit -- if the wielder isn't locatable this tick we
    /// retry next tick (and battle exit's ReleaseAll is the backstop), so a faded buff never lingers.</summary>
    private void ExpireAll()
    {
        if (!SameWielder()) return;
        int turn = _turns.GlobalTurns;
        List<(int off, byte mask)>? drop = null;
        foreach (var key in _state.Held)
            if (LarcenyPolicy.IsExpired(turn, _state.StolenAt(key), Tuning.LarcenyHoldTurns))
                (drop ??= new()).Add(key);
        if (drop is null) return;
        foreach (var key in drop)
        {
            LarcenyPolicy.ClearBit(_mem, _wielderAddr, key.off, key.mask);
            _state.Release(key);
            Log.Info($"larceny: a stolen buff faded from the wielder after ~{Tuning.LarcenyHoldTurns} turns");
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
