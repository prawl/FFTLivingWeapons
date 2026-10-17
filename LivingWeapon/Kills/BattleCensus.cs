using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Reliquary P2 probe (docs/RELIQUARY_AC.md P2). Read-only identity census of both teams, taken once per
/// battle when <see cref="EnemyOracle.CoverageDone"/> goes true (the "all N enemies accounted for" edge,
/// EnemyOracle.CheckCoverage, about 5s into battle), so a boss nameId can be checked for uniqueness and
/// player-pool collisions. <see cref="Tick"/> is wired from KillTracker.Poll after ScanCorpses.
/// <see cref="EmitExit"/> runs the census on the battle-exit edge even if coverage never
/// completed; a battle where both run logs two records. Two guarded passes over the band (nameId, job,
/// level/brave/faith, hp, grid position) and the roster (the same, plus the raw hand slots).
/// One Debug line per slot and a single flight tap for the whole census.
/// </summary>
internal sealed class BattleCensus
{
    // Caps the flight record (the ring holds 4096). Raised 1400 -> 1800 when the
    // roster part gained the W{rHand},{lHand},{offHand} tail that lands after the band part and would
    // be truncated first on a full battle.
    private const int MaxPayloadChars = 1800;

    private readonly IGameMemory _mem;
    private readonly Action<string, string>? _recorder;

    private bool _fired;

    public BattleCensus(IGameMemory mem, Action<string, string>? recorder)
    {
        _mem = mem;
        _recorder = recorder;
    }

    /// <summary>Runs the census the first time <paramref name="coverageDone"/> reads true this
    /// battle, then does nothing until <see cref="ResetBattle"/>.</summary>
    public void Tick(bool coverageDone)
    {
        if (_fired || !coverageDone) return;
        _fired = true;
        RunCensus();
    }

    /// <summary>Re-arm for the next battle.</summary>
    public void ResetBattle() => _fired = false;

    /// <summary>Run the census regardless of <see cref="_fired"/>, on the battle-exit
    /// edge, so the exit tape always has one.</summary>
    internal void EmitExit() => RunCensus();

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
                byte job = _mem.Readable(addr + Offsets.AJob, 1) ? _mem.U8(addr + Offsets.AJob) : (byte)0;
                int lvl = _mem.U8(addr + Offsets.ALevel);
                int br = _mem.U8(addr + Offsets.ABrave);
                int fa = _mem.U8(addr + Offsets.AFaith);
                int hp = _mem.U16(addr + Offsets.AHp);
                int mhp = _mem.U16(addr + Offsets.AMaxHp);
                int gx = _mem.U8(addr + Offsets.AGx);
                int gy = _mem.U8(addr + Offsets.AGy);

                ModLogger.Debug(LogVerb.Trace, $"census: slot={s} nameId={nameId} job={job} lvl={lvl} br={br} fa={fa} hp={hp} mhp={mhp} at=({gx},{gy})");
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
                int rbrave = _mem.U8(b + Offsets.RBrave);
                int rfaith = _mem.U8(b + Offsets.RFaith);
                int rh = _mem.U16(b + Offsets.RRHand);
                int lh = _mem.U16(b + Offsets.RLHand);
                int oh = _mem.U16(b + Offsets.ROffHand);
                ModLogger.Debug(LogVerb.Trace, $"census: roster slot={s} nameId={nameId} level={rlvl} brave={rbrave} faith={rfaith} rHand={rh} lHand={lh} offHand={oh}");
                // Level/brave/faith let the fingerprint rescue's fp= field be checked against
                // the row; round 2 adds the raw hand ids for the weapon-key rescue's wpn= tap.
                rosterParts.Add($"{s}:{nameId}L{rlvl}B{rbrave}F{rfaith}W{rh},{lh},{oh}");
                rosterCount++;
            }

            string payload = "band " + string.Join(" ", bandParts) + " | roster " + string.Join(" ", rosterParts);
            if (payload.Length > MaxPayloadChars) payload = payload.Substring(0, MaxPayloadChars) + "...";
            _recorder?.Invoke("census", payload);

            ModLogger.Debug(LogVerb.Trace, $"census: {bandCount} band units, {rosterCount} roster slots dumped.");
        }
        catch (Exception ex)
        {
            // Warn, not Error, so a failed probe does not use up the launch's one FlushOnce archive.
            ModLogger.Warn(LogVerb.Trace, "The identity census failed and was skipped: " + ex.Message);
        }
    }
}
