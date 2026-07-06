#if LWDEV
using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// DEV-ONLY passive recorder (LW-31 stage 2's live-pass blocker, docs/TODO.md Now entry).
/// Unlike its sibling spikes (HeaderSpike/AttackCardSpike), this one is PASSIVE: no keybind, no
/// heap scan, no write. It exists purely to log evidence for an offline adjudication; see
/// TurnOwnerProbe.cs's class doc for the full purpose, the two hypotheses, and the correlation
/// method (lining this instrument's tapes up against AttackCard.Paint.cs's own "attack-card desc
/// repainted" log line, which marks a menu-hover moment).
///
/// Ticks in-battle only (Engine.cs's in-battle spike block): the Abilities menu this research
/// targets only exists in battle, so an out-of-battle tick would just waste reads. Throttled to
/// at most <see cref="SamplesPerSecond"/> samples/second via TurnOwnerProbe.ShouldSample, so a
/// long recorded battle does not flood the log with redundant ticks between real changes.
///
/// Each sample reads three channels and logs one Trace line PER CHANNEL, and only when that
/// channel's own value changed since its last logged value (TurnOwnerProbe.Changed; change-only,
/// mirrors the sibling spikes' revert watches):
///
/// 1. Band CT: every valid band slot (Band.Entry + Band.IsValid, the same loop shape
///    KillTracker.Corpses.ScanCorpses walks), each slot's CT at band+0x25 (Offsets.ACtSlam) and
///    identity (level/brave/faith), plus the single global acted byte (Offsets.Acted, the same
///    field KillTracker gates its acted-period latch on).
/// 2. Cursor-follower struct: 64 guarded bytes at Offsets.TurnQueue, the condensed active-unit
///    struct hypothesis 2 is testing.
/// 3. Register baseline: the injected ActorRegister's LastPlayerNameId/LastPlayerArrivalTick/
///    Trusted, the SAME register instance KillerStamp and AttackCard already trust (no second
///    register is constructed here).
/// </summary>
internal sealed class TurnOwnerSpike
{
    private const int SamplesPerSecond = 4;
    private const int CursorReadLen = 64;

    private readonly IGameMemory _mem;
    private readonly ActorRegister _register;

    private long? _lastSampleMs;
    private string? _lastCtSnapshot;
    private byte[]? _lastCursorBytes;
    private bool _cursorWasReadable = true;   // starts true so a first-sample read failure logs a transition
    private string? _lastRegisterSnapshot;

    public TurnOwnerSpike(IGameMemory mem, ActorRegister register)
    {
        _mem = mem;
        _register = register;
        ModLogger.Debug(LogVerb.Trace,
            "turn-owner-probe: armed (passive, no keybind; records band CT, the cursor-follower struct, and the actor register on change, throttled to 4 samples per second)");
    }

    /// <summary>In-battle tick, throttled internally. See the class doc for the three channels.</summary>
    public void Tick()
    {
        long now = Environment.TickCount64;
        if (!TurnOwnerProbe.ShouldSample(now, _lastSampleMs, SamplesPerSecond)) return;
        _lastSampleMs = now;

        SampleBandCt();
        SampleCursorStruct();
        SampleRegister();
    }

    /// <summary>Hypothesis 1: the band CT channel, one line for every valid slot's identity and
    /// CT, plus the global acted byte.</summary>
    private void SampleBandCt()
    {
        int acted = _mem.U8(Offsets.Acted);
        var slots = new List<(int slot, int lvl, int br, int fa, int ct)>();
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Band.Entry(s);
            if (!Band.IsValid(_mem, addr)) continue;

            int lvl = _mem.U8(addr + Offsets.ALevel);
            int br = _mem.U8(addr + Offsets.ABrave);
            int fa = _mem.U8(addr + Offsets.AFaith);
            int ct = _mem.U8(addr + Offsets.ACtSlam);
            slots.Add((s, lvl, br, fa, ct));
        }

        string snapshot = TurnOwnerProbe.FormatCtSnapshot(slots, acted);
        if (!TurnOwnerProbe.Changed(_lastCtSnapshot, snapshot)) return;
        _lastCtSnapshot = snapshot;
        ModLogger.Debug(LogVerb.Trace, snapshot);
    }

    /// <summary>Hypothesis 2: the cursor-follower struct, read whole (guarded) rather than
    /// field-by-field, so an unexpected byte still shows up in the dump.</summary>
    private void SampleCursorStruct()
    {
        if (!_mem.TryReadBytes(Offsets.TurnQueue, CursorReadLen, out var buf))
        {
            if (_cursorWasReadable)
            {
                _cursorWasReadable = false;
                _lastCursorBytes = null;   // force a fresh log on recovery, even if bytes end up identical
                ModLogger.Debug(LogVerb.Trace, "turn-owner-probe: cursor struct became unreadable");
            }
            return;
        }
        _cursorWasReadable = true;

        if (!TurnOwnerProbe.Changed(_lastCursorBytes, buf)) return;
        _lastCursorBytes = buf;
        ModLogger.Debug(LogVerb.Trace, TurnOwnerProbe.FormatCursorDump(buf));
    }

    /// <summary>The register baseline this instrument watches purely for the correlation tape;
    /// it never mutates the register (Update/ResetBattle stay KillTracker's own responsibility).</summary>
    private void SampleRegister()
    {
        string snapshot = TurnOwnerProbe.FormatRegisterSnapshot(
            _register.LastPlayerNameId, _register.LastPlayerArrivalTick, _register.Trusted);
        if (!TurnOwnerProbe.Changed(_lastRegisterSnapshot, snapshot)) return;
        _lastRegisterSnapshot = snapshot;
        ModLogger.Debug(LogVerb.Trace, snapshot);
    }
}
#endif
