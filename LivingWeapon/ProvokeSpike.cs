#if LWDEV
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace LivingWeapon;

/// <summary>
/// DEV-ONLY (LW-123 arc 2a, owner-decided 2026-07-22): arm ProvokeHold without arc 2b's real granted
/// command (which does not exist yet -- items.json id 33 carries no signature block). Plants the SAME
/// mark arc 1's real cast will eventually plant -- composed +0x45/0x80 and inflicted +0x1D3/0x80, the
/// blank status id 0 (<see cref="ProvokeHold.MarkId"/>) -- on an enemy, via two guarded bit writes
/// (<see cref="MemBits.OrSet"/>, never an unguarded deref). ProvokeHold itself gates on the mark bit,
/// not on this class, so the production hold engine is unchanged by this spike's presence.
///
/// TWO trigger lanes, StatusSpike's precedent: the F6 key (VK 0x75) and a marker-file lane
/// (<c>&lt;modDir&gt;/provoke_request.txt</c>, polled every ~0.5s, deleted on read so a stale
/// request can never re-fire). Moved off F3 to F6 2026-07-22: F3 never registered on the owner's
/// box (this machine has a history of eaten F keys, owner-reported), and F6 is now free -- LW-67
/// stripped the old Flavor/Header/AttackCard F6 spikes, so the earlier "F6 is taken" note (and the
/// stale twin in BodyDoubleSpike) was wrong. No env vars: they do not survive this game's launch chain.
///
/// TARGET SELECTION: the HOVERED enemy if a cursor read is available -- while paused, the condensed
/// TurnQueue struct tracks the unit under the cursor (memory "condensed-struct-follows-hover"),
/// matched to an enemy band seat by maxHp+level -- else the FIRST live enemy via a plain enemy-side
/// band scan. Deliberately NOT StatusSpike.FindEnemy: that helper is cold-call-scoped to callable
/// engine slots (band seats 8..28); this spike makes no cold call, so every band seat is fair game.
///
/// GUARDS (StatusSpike's discipline, minus the cold-call-specific ones this spike does not need: no
/// TargetReady prologue check, no paused-menu reentrancy requirement -- a guarded byte write is not a
/// cold call): fires only while <see cref="Mem.WritesEnabled"/>, only during a genuine inLive frame,
/// and only while the game window is foreground (never while alt-tabbed).
/// </summary>
internal sealed class ProvokeSpike
{
    private const int VkF6 = 0x75;   // F6: owner-confirmed working on this box; F3 was eaten (see class doc)

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

    /// <summary>In-battle loop tick: heartbeat, the F6 edge, and the ~0.5s marker-file poll. Mirrors
    /// StatusSpike.Tick's shape exactly (armed announce, heartbeat, then key/request checks gated on
    /// a genuine inLive frame).</summary>
    public void Tick(bool inLive)
    {
        if (!_announced)
        {
            _announced = true;
            ModLogger.Debug(LogVerb.Trace, "provoke-spike: armed (dev). F6 or a provoke_request.txt drop marks the hovered-else-first-live enemy so ProvokeHold arms without the real granted command (arc 2b, not wired yet). THROWAWAY SAVE ONLY.");
        }
        if (++_hbTick % 300 == 0)   // ~10s at 33ms
            ModLogger.Debug(LogVerb.Trace, $"provoke-spike: alive (writes {(Mem.WritesEnabled ? "on" : "OFF")})");

        if (!inLive) return;
        if (Pressed(VkF6, ref _f6Was)) Fire("F6");
        if (_hbTick % 15 == 0) PollRequest();   // ~0.5s: responsive without hammering the disk
    }

    /// <summary>Consume a queued request file (any content -- presence alone is the trigger, unlike
    /// StatusSpike's id-carrying requests: the mark is always the same blank status). Deleted FIRST
    /// so a stale request can never fire twice; a delete/read failure is a logged no-op.</summary>
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
    /// (memory "condensed-struct-follows-hover"): match it to an enemy band seat by maxHp+level. Any
    /// out-of-band read (not paused, or a garbage struct) simply misses -- no target, not a crash.</summary>
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

    /// <summary>Plain enemy-side band scan for the first LIVE (HP &gt; 0) seat -- every seat is fair
    /// game (no cold-call-scoped range restriction; see the class doc for why that differs from
    /// StatusSpike.FindEnemy).</summary>
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
