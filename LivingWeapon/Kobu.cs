using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Kiyomori's "Kobu" signature: while a +3 Kiyomori is wielded as the main hand, whenever
/// the wielder's action damages an enemy whose CURRENT brave (band +0x0F) exceeds the
/// wielder's accumulated max, the wielder's current brave is raised to match (climb-only,
/// capped at Tuning.KobuBraveCap, battle-scoped). Katana formula 1: PA x Brave/100 x WP --
/// striking a braver foe sharpens the blade for the rest of the fight.
///
/// ADDRESSING -- all reads/writes are BAND-ENTRY-RELATIVE (band_entry = combat_base + 0x1C):
///   Current brave = band +0x0F (Offsets.ABraveCurrent). Read from the enemy; write-held on the wielder.
///   Orig brave    = band +0x0E (Offsets.ABrave). NEVER write: it re-normalizes, never displays,
///                   and is the Wielder.Locate fingerprint -- writing it would break the next locate.
///   NEVER use +0x2B off a band address: band_entry+0x2B = combat+0x47 = the Reraise/Invisible/Float
///   STATUS bitfield (Offsets.AReraise), not brave. Proven layout: brave-faith-current-vs-orig-offsets.
///
/// DETECTION: mirrors Maim's HP-diff band scan (Maim.cs) under the acting-main-hand gate.
///   HP-diff tracking (RicochetState) baselines every on-field tick; a drop + enemy fingerprint +
///   active gate fires the brave-update. One module per hit: the first tick baselines silently,
///   the next tick with a HP drop detects the hit -- same two-tick window as Maim/Ricochet.
/// WIELDER LOCATE: Wielder.ResolveDeployedMainHand each tick (single deployed main-hand wielder).
/// HOLD: each tick after the scan, if the live band +0x0F is below _maxBrave, write _maxBrave
///   (guarded W8). The engine may re-normalize between our 33 ms ticks; the hold re-stamps.
/// RESET: ResetBattle() clears _maxBrave; the combat struct is rebuilt each battle so the engine's
///   re-normalization combined with our cleared ceiling is a clean slate.
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
    private int _maxBrave;   // 0 = uninitialized (seeded from wielder's natural brave on first sight)

    public Kobu(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, KillTracker tracker,
                IGameMemory? mem = null)
    {
        _mem = mem ?? new LiveMemory();
        _meta = meta;
        _kills = kills;
        _tracker = tracker;
        _hpState = new RicochetState(Offsets.BandSlots);
    }

    public void ResetBattle()
    {
        _maxBrave = 0;
        _hpState.ResetBattle();
    }

    public void Tick(bool onField)
    {
        if (!_meta.TryGetValue(KiyomoriId, out var m) || m.Signature is null) return;
        if (!IsActive(m.Signature, Tuning.TierOf(_kills, KiyomoriId))) return;

        // Locate the single deployed main-hand wielder of Kiyomori (needed to seed and hold brave)
        long wielderEntry = Wielder.ResolveDeployedMainHand(_mem, KiyomoriId, out _);
        if (wielderEntry == 0) return;

        // Seed _maxBrave from the wielder's natural current brave on the first tick of each battle
        if (_maxBrave == 0)
            _maxBrave = _mem.U8(wielderEntry + Offsets.ABraveCurrent);

        // Acting gate: this tick is the wielder's action, on the live field, and the acted flag is set
        bool active = onField
                      && Signatures.IsActingMainHand(_tracker.LastPlayerMainHand, KiyomoriId)
                      && _mem.U8(Offsets.Acted) == 1;

        if (onField)
        {
            // Scan band for HP drops. Observe baselines every on-field tick so the window is fresh
            // when the active gate opens. Brave-update fires only under the active gate (the wielder's
            // own hit); a heal (negative delta) or a hit by someone else is ignored.
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
                if (!enemyFps.Contains((mhp, lvl, br, fa))) continue;

                // Read the struck enemy's CURRENT brave (band +0x0F, NOT the orig-brave fingerprint)
                int struckBrave = _mem.U8(addr + Offsets.ABraveCurrent);
                int prev = _maxBrave;
                _maxBrave = NextMax(_maxBrave, struckBrave, Tuning.KobuBraveCap);
                if (_maxBrave != prev)
                    Log.Info($"kobu: struck enemy (orig brave {br}, current brave {struckBrave}) -- wielder brave ceiling raised {prev} -> {_maxBrave}");
            }
        }

        // Hold: write _maxBrave to the wielder's current-brave byte every tick it re-normalizes below target
        int liveBrave = _mem.U8(wielderEntry + Offsets.ABraveCurrent);
        if (ShouldRaise(liveBrave, _maxBrave))
        {
            long target = wielderEntry + Offsets.ABraveCurrent;
            if (_mem.Writable(target, 1))
                _mem.W8(target, (byte)_maxBrave);
        }
    }
}
