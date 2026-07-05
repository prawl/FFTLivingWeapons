using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Reliquary P2 probe instrument (docs/RELIQUARY_AC.md P2) -- once-per-battle both-teams identity
/// census at the oracle coverage-complete edge; read-only; exists so a named boss's nameId can be
/// checked for uniqueness/stability/player-pool collisions without killing every unit.
///
/// TRIGGER: <see cref="EnemyOracle.CoverageDone"/> -- the "kill: all N enemies accounted for" edge,
/// live-proven to fire reliably ~5s into battle (EnemyOracle.CheckCoverage). Firing on this existing
/// edge means the census never needs its own timer: it rides a trigger that is already known-solid.
/// Fires EXACTLY ONCE per battle (armed/fired flag, not a re-derived edge test -- once
/// <see cref="EnemyOracle.CoverageDone"/> goes true it stays true for the rest of the battle, so a
/// simple fired-once latch is equivalent to a strict false-&gt;true transition test and simpler to
/// reason about). Wired from KillTracker.Poll, right after ScanCorpses so the oracle has ticked this
/// frame before <see cref="Tick"/> reads it. Inert after firing until <see cref="ResetBattle"/>.
///
/// Two read-only, guarded passes (never throws -- mirrors VictimProbe/FlavorSpike's try/catch shape):
///   BAND   -- every valid band slot (<see cref="Band.IsValid"/>), both teams as the live auth-band
///             frames stand this tick: nameId, job byte, level/brave/faith, hp/maxHp, grid position.
///   ROSTER -- every occupied roster slot (RLevel 1..99 -- the same occupied-check
///             ActorRegister.Bridge uses), its RNameId: the player-pool side of the P2 collision
///             check.
/// Logged one ModLogger.LogDebug line per slot (file-only unless VerboseLog is on), plus ONE bounded
/// flight-recorder tap for the whole census -- a per-slot tap would flood the bounded 4096-record
/// ring for what is fundamentally one event.
/// </summary>
internal sealed class BattleCensus
{
    // Keep the single flight record compact regardless of how many slots are occupied -- the
    // ring is bounded (FlightRecorder, 4096 records) and a full-roster, full-band battle could
    // otherwise produce a payload far larger than every other tap in the file.
    private const int MaxPayloadChars = 900;

    private readonly IGameMemory _mem;
    private readonly Action<string, string>? _recorder;

    private bool _fired;

    public BattleCensus(IGameMemory mem, Action<string, string>? recorder)
    {
        _mem = mem;
        _recorder = recorder;
    }

    /// <summary>One tick: runs the census the first time <paramref name="coverageDone"/> reads true
    /// this battle, then goes inert (no-op) for every subsequent call until <see cref="ResetBattle"/>
    /// re-arms it.</summary>
    public void Tick(bool coverageDone)
    {
        if (_fired || !coverageDone) return;
        _fired = true;
        RunCensus();
    }

    /// <summary>Re-arm for the next battle.</summary>
    public void ResetBattle() => _fired = false;

    private void RunCensus()
    {
        try
        {
            var bandParts = new List<string>();
            int bandCount = 0;
            for (int s = 0; s < Offsets.BandSlots; s++)
            {
                long addr = Band.Entry(s);
                if (!Band.IsValid(_mem, addr)) continue;

                bool nameOk = _mem.Readable(addr + Offsets.ANameId, 2);
                ushort nameId = nameOk ? _mem.U16(addr + Offsets.ANameId) : (ushort)0;
                byte job = _mem.Readable(addr + Puppeteer.JobOff, 1) ? _mem.U8(addr + Puppeteer.JobOff) : (byte)0;
                int lvl = _mem.U8(addr + Offsets.ALevel);
                int br = _mem.U8(addr + Offsets.ABrave);
                int fa = _mem.U8(addr + Offsets.AFaith);
                int hp = _mem.U16(addr + Offsets.AHp);
                int mhp = _mem.U16(addr + Offsets.AMaxHp);
                int gx = _mem.U8(addr + Offsets.AGx);
                int gy = _mem.U8(addr + Offsets.AGy);

                ModLogger.LogDebug($"census: slot={s} nameId={nameId} job={job} lvl={lvl} br={br} fa={fa} hp={hp} mhp={mhp} at=({gx},{gy})");
                bandParts.Add($"s{s}:{nameId}/{job}");
                bandCount++;
            }

            var rosterParts = new List<string>();
            int rosterCount = 0;
            for (int s = 0; s < Offsets.RosterSlots; s++)
            {
                long b = Offsets.RosterBase + (long)s * Offsets.RosterStride;
                int rlvl = _mem.U8(b + Offsets.RLevel);
                if (rlvl < 1 || rlvl > 99) continue;   // unoccupied slot

                ushort nameId = _mem.U16(b + Offsets.RNameId);
                ModLogger.LogDebug($"census: roster slot={s} nameId={nameId}");
                rosterParts.Add($"{s}:{nameId}");
                rosterCount++;
            }

            string payload = "band " + string.Join(" ", bandParts) + " | roster " + string.Join(" ", rosterParts);
            if (payload.Length > MaxPayloadChars) payload = payload.Substring(0, MaxPayloadChars) + "...";
            _recorder?.Invoke("census", payload);

            ModLogger.LogDebug($"census: {bandCount} band units, {rosterCount} roster slots dumped.");
        }
        catch (Exception ex)
        {
            ModLogger.LogError("census: failed -- " + ex.Message);
        }
    }
}
