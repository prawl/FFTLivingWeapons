using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Reliquary P2 probe instrument (docs/RELIQUARY_AC.md P2) -- once-per-battle both-teams identity
/// census at the oracle coverage-complete edge; read-only; exists so a named boss's nameId can be
/// checked for uniqueness/stability/player-pool collisions without killing every unit.
///
/// TRIGGER: <see cref="EnemyOracle.CoverageDone"/> -- the "kill: all N enemies accounted for" edge,
/// observed live to fire reliably ~5s into battle (EnemyOracle.CheckCoverage). Firing on this existing
/// edge means the census never needs its own timer: it rides a trigger that is already known-solid.
/// Fires EXACTLY ONCE per battle via <see cref="Tick"/> (armed/fired flag, not a re-derived edge
/// test: once <see cref="EnemyOracle.CoverageDone"/> goes true it stays true for the rest of the
/// battle, so a simple fired-once latch is equivalent to a strict false-&gt;true transition test and
/// simpler to reason about). Wired from KillTracker.Poll, right after ScanCorpses so the oracle has
/// ticked this frame before <see cref="Tick"/> reads it.
///
/// LW-56 D11/A3: <see cref="Tick"/>'s trigger never fires at all when oracle coverage never
/// completes (an over-count keeps CheckCoverage from ever passing cleanly), so a battle can run
/// its whole length with zero census records. <see cref="EmitExit"/> is the repair: it runs the
/// census UNCONDITIONALLY, bypassing the fired latch, called by Engine on the battle-exit edge
/// (before the flight flush) so every exit tape carries a fresh census regardless of whether
/// <see cref="Tick"/> ever fired. A battle where both run produces two census records; that is a
/// count change, not a type change (the record type stays "census").
///
/// Two read-only, guarded passes (never throws -- mirrors VictimProbe/FlavorSpike's try/catch shape):
///   BAND   -- every valid band slot (<see cref="Band.IsValid"/>), both teams as the live auth-band
///             frames stand this tick: nameId, job byte, level/brave/faith, hp/maxHp, grid position.
///   ROSTER -- every occupied roster slot (RLevel 1..99 -- the same occupied-check
///             ActorRegister.Bridge uses): RNameId, level, brave, faith, and (LW-56 round 2) the
///             RAW u16 reads of all three hand slots, sentinels included exactly as memory held
///             them (`{slot}:{nameId}L{level}B{brave}F{faith}W{rHand},{lHand},{offHand}`, e.g.
///             "0:1L99B89F76W80,65535,65535"): the player-pool side of the P2 collision check,
///             and the exact evidence the weapon-key rescue's `wpn=` tape field can be
///             cross-checked against.
/// Logged one ModLogger.Debug line per slot (file-only unless the console level is raised to Debug), plus ONE bounded
/// flight-recorder tap for the whole census -- a per-slot tap would flood the bounded 4096-record
/// ring for what is fundamentally one event.
/// </summary>
internal sealed class BattleCensus
{
    // Keep the single flight record compact regardless of how many slots are occupied -- the
    // ring is bounded (FlightRecorder, 4096 records) and a full-roster, full-band battle could
    // otherwise produce a payload far larger than every other tap in the file. Raised 1400 -> 1800
    // (LW-56 round 2) when the roster part grew a W{rHand},{lHand},{offHand} tail: a fully
    // populated battle (max band + 20 roster rows, every hand slot at its widest raw reading) now
    // runs noticeably larger than the pre-round-2 ~1.2k chars, and the new tail lands AFTER the
    // band part, so the old cap would have truncated it away on exactly the busiest battles.
    private const int MaxPayloadChars = 1800;

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

    /// <summary>LW-56 D11/A3: run the census unconditionally, bypassing <see cref="_fired"/>.
    /// Called on the battle-exit edge so an exit tape always carries a fresh census, whether or
    /// not <see cref="Tick"/> ever fired this battle.</summary>
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
                byte job = _mem.Readable(addr + Puppeteer.JobOff, 1) ? _mem.U8(addr + Puppeteer.JobOff) : (byte)0;
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
                // LW-56: level+brave+faith ride alongside nameId so a stale roster is visible on
                // tape by all four fields together (not nameId alone), and so the fingerprint
                // rescue's fp=L{level}B{brave}F{faith} can be cross-checked against the exact
                // roster row a tape names. Round 2: the raw hand ids (sentinels included, exactly
                // what memory held) so the weapon-key rescue's wpn= tap can be cross-checked
                // against the exact row it claims to have matched.
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
            // Warning, not Error: a read-only probe failing degrades evidence, it breaks nothing,
            // and it must not burn the launch's one FlushOnce flight archive.
            ModLogger.Warn(LogVerb.Trace, "The identity census failed and was skipped: " + ex.Message);
        }
    }
}
