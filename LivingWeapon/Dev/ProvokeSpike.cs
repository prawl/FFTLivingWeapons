#if LWDEV
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace LivingWeapon;

/// <summary>
/// Dev-only spike that arms ProvokeHold without the real granted command (added 2026-07-22).
/// That makes the hold testable on any save and job. Plants the mark Provoke.cs's cast plants (composed +0x45/0x80,
/// inflicted +0x1D3/0x80, blank status id 0, <see cref="ProvokeHold.MarkId"/>) on an enemy with two guarded
/// <see cref="MemBits.OrSet"/> writes. ProvokeHold gates on the mark bit, so it is unchanged by this spike. Triggers as in
/// StatusSpike. F6 (VK 0x75) and a marker file (<c>&lt;modDir&gt;/provoke_request.txt</c>, polled ~0.5s, deleted on read).
/// Moved off F3 on 2026-07-22 because F3 never registered on the owner's box; F6 is free since the old F6 spikes were
/// stripped. No env vars. They do not survive the game's launch chain. The target is the hovered enemy if available (while
/// paused, the condensed TurnQueue struct tracks the cursor, memory "condensed-struct-follows-hover", matched to a band seat
/// by maxHp+level), else the first live enemy. Not StatusSpike.FindEnemy, which is scoped to cold-callable seats 8..28; this
/// spike makes no cold call. Fires only while <see cref="Mem.WritesEnabled"/>, in a genuine inLive frame, with the game
/// foreground.
/// </summary>
internal sealed class ProvokeSpike
{
    private const int VkF6 = 0x75;   // F6. Owner-confirmed working on this box; F3 was eaten (see class doc)

    private readonly IGameMemory _mem;
    private readonly string? _requestPath;
    private bool _f6Was;
    private int _hbTick;
    private bool _announced;

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();

    public ProvokeSpike(IGameMemory mem, string? modDir = null)
    {
        _mem = mem;
        _requestPath = string.IsNullOrEmpty(modDir) ? null : Path.Combine(modDir, "provoke_request.txt");
    }

    /// <summary>In-battle loop tick. Heartbeat, the F6 edge, and the ~0.5s marker-file poll, same shape
    /// as StatusSpike.Tick.</summary>
    public void Tick(bool inLive)
    {
        if (!_announced)
        {
            _announced = true;
            ModLogger.Debug(LogVerb.Trace, "provoke-spike: armed (dev). F6 or a provoke_request.txt drop marks the hovered-else-first-live enemy so ProvokeHold arms without casting the real granted command (which does ship: this lane is for saves where it cannot be reached). THROWAWAY SAVE ONLY.");
        }
        if (++_hbTick % 300 == 0)   // ~10s at 33ms
            ModLogger.Debug(LogVerb.Trace, $"provoke-spike: alive (writes {(Mem.WritesEnabled ? "on" : "OFF")})");

        if (!inLive) return;
        if (Pressed(VkF6, ref _f6Was)) Fire("F6");
        if (_hbTick % 15 == 0) PollRequest();   // ~0.5s. Responsive without hammering the disk
    }

    /// <summary>Consume a queued request file (presence alone is the trigger). Deleted first so a stale
    /// request cannot fire twice; a delete/read failure is a logged no-op.</summary>
    private void PollRequest()
    {
        if (_requestPath == null) return;
        try
        {
            if (!File.Exists(_requestPath)) return;
            File.Delete(_requestPath);
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Trace, $"provoke-spike: could not read/delete the request file: {ex.Message}");
            return;
        }
        Fire("REQUEST");
    }

    private static bool Pressed(int vk, ref bool was)
    {
        bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
        bool pressed = down && !was && GameIsForeground();   // never fire while alt-tabbed
        was = down;
        return pressed;
    }

    private static bool GameIsForeground()
    {
        nint hwnd = GetForegroundWindow();
        if (hwnd == 0) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == GetCurrentProcessId();
    }

    /// <summary>While paused, the condensed TurnQueue struct tracks the unit under the cursor
    /// (memory "condensed-struct-follows-hover"); match it to an enemy band seat by maxHp+level. An
    /// out-of-band read just misses.</summary>
    private bool TryFindHoveredEnemy(out long entry)
    {
        entry = 0;
        if (Mem.U8(Offsets.PauseFlag) != 1) return false;
        int mhp = _mem.U16(Offsets.TurnQueue + Offsets.TqMaxHp);
        int lvl = _mem.U16(Offsets.TurnQueue + Offsets.TqLevel);
        if (mhp < 1 || mhp > 1999 || lvl < 1 || lvl > 99) return false;
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (!Band.IsValid(_mem, e)) continue;
            if ((_mem.U8(e + Offsets.AFriendFoe) & Offsets.AFriendFoeEnemyBit) == 0) continue;
            if (_mem.U16(e + Offsets.AMaxHp) != mhp || _mem.U8(e + Offsets.ALevel) != lvl) continue;
            entry = e;
            return true;
        }
        return false;
    }

    /// <summary>First live (HP &gt; 0) enemy-side band seat; no range restriction (see class doc).</summary>
    private long FindFirstLiveEnemy()
    {
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (!Band.IsValid(_mem, e)) continue;
            if ((_mem.U8(e + Offsets.AFriendFoe) & Offsets.AFriendFoeEnemyBit) == 0) continue;
            if (_mem.U16(e + Offsets.AHp) == 0) continue;
            return e;
        }
        return 0;
    }

    private void Fire(string label)
    {
        if (!Mem.WritesEnabled)
        {
            ModLogger.Event(LogVerb.Trace, $"provoke-spike: {label} ignored; the fingerprint guard has not armed yet (no writes).");
            return;
        }
        if (!GameIsForeground())
        {
            ModLogger.Event(LogVerb.Trace, $"provoke-spike: {label} ignored; the game window is not foreground.");
            return;
        }
        bool hovered = TryFindHoveredEnemy(out long entry);
        if (!hovered) entry = FindFirstLiveEnemy();
        if (entry == 0)
        {
            ModLogger.Event(LogVerb.Trace, $"provoke-spike: {label} found no usable enemy (none live in the band right now).");
            return;
        }

        int by = StatusApply.StatusByte(ProvokeHold.MarkId);
        byte mask = StatusApply.StatusMask(ProvokeHold.MarkId);
        bool composedOk = MemBits.OrSet(entry + StatusApply.Composed + by, mask, out _);
        bool inflictedOk = MemBits.OrSet(entry + StatusApply.Inflicted + by, mask, out _);
        int gx = _mem.U8(entry + Offsets.AGx), gy = _mem.U8(entry + Offsets.AGy);
        ModLogger.Event(LogVerb.Trace,
            $"provoke-spike: {label} marked the {(hovered ? "hovered" : "first live")} enemy at tile ({gx},{gy}); composed={composedOk} inflicted={inflictedOk}. ProvokeHold should arm on its next tick.");
    }
}
#endif
