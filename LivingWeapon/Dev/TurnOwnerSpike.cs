#if LWDEV
using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Dev-only passive recorder for the Attack-menu painter (which weapon dossier the Attack card resolves at menu-open),
/// shipped 2b2f5b4 on 2026-07-07. It still ticks as a correlation recorder for the backlog's
/// turn-order rebuild; unwire it if that declines. TurnOwnerProbe.cs documents the two hypotheses
/// and how the tapes line up with AttackCard.Paint.cs's "attack-card desc repainted" log line.
/// In-battle only, throttled to <see cref="SamplesPerSecond"/>. One Trace line per channel, only on change
/// (TurnOwnerProbe.Changed). Channels are band CT per slot at band+0x25 (Offsets.ACtSlam) plus the acted byte
/// (Offsets.Acted), the 64-byte cursor-follower struct at Offsets.TurnQueue, and the ActorRegister baseline.
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

    /// <summary>In-battle tick, throttled internally.</summary>
    public void Tick()
    {
        long now = Environment.TickCount64;
        if (!TurnOwnerProbe.ShouldSample(now, _lastSampleMs, SamplesPerSecond)) return;
        _lastSampleMs = now;

        SampleBandCt();
        SampleCursorStruct();
        SampleRegister();
    }

    /// <summary>Hypothesis 1, band CT per valid slot plus the acted byte.</summary>
    private void SampleBandCt()
    {
        int acted = _mem.U8(Offsets.Acted);
        var slots = new List<(int slot, int lvl, int br, int fa, int slam, int turn, int kind, int idx)>();
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Band.Entry(s);
            if (!Band.IsValid(_mem, addr)) continue;

            int lvl = _mem.U8(addr + Offsets.ALevel);
            int br = _mem.U8(addr + Offsets.ABrave);
            int fa = _mem.U8(addr + Offsets.AFaith);
            int slam = _mem.U8(addr + Offsets.ACtSlam);           // hypothesis 1. Scheduler CT (0x25), player-unreliable
            int turn = _mem.U8(addr + Offsets.ACtTurn);           // hypothesis 3. Per-unit completed-turn CT (0x09)
            int kind = _mem.U8(addr + Offsets.AArec + 0xA);       // hypothesis 4. AArec kind (5=performing/6=receiving)
            int idx = _mem.U8(addr + Offsets.AArec);              // AArec idx (== seat-8): which unit the record is for
            slots.Add((s, lvl, br, fa, slam, turn, kind, idx));
        }

        string snapshot = TurnOwnerProbe.FormatCtSnapshot(slots, acted);
        if (!TurnOwnerProbe.Changed(_lastCtSnapshot, snapshot)) return;
        _lastCtSnapshot = snapshot;
        ModLogger.Debug(LogVerb.Trace, snapshot);
    }

    /// <summary>Hypothesis 2, the cursor struct read whole so unexpected bytes show in the dump.</summary>
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

    /// <summary>Register baseline for the correlation tape; read-only.</summary>
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
