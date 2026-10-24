using System;
using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace LivingWeapon;

/// <summary>
/// Native half of the facing-prompt swap. Installs the SetTextString detour and swaps in the pinned buffer;
/// PromptSwap makes every decision. Always forwards to the original, so a managed fault cannot swallow the
/// game's prompt.
/// </summary>
internal sealed class PromptSwapHook
{
    // The game function that sets a prompt's text, hooked so a toast can replace the words before they draw.
    // Args are (holder, char* text, r8, r9). Pointing rdx at a pinned ANSI buffer here renders in the prompt
    // slot until the player confirms facing (docs/research/CALLOUT_BANNER_JOURNEY.md). r8 and r9 pass through.
    //
    // The 1.5.1 entry 0x14028F750 is a dispatch wrapper taking (holder in rcx, flag byte in dl), so a
    // hook there sees caller garbage in rdx. The wrapper resolves the text from a std::string at
    // holder+0x20 (size +0x30, capacity +0x38, inline when capacity is below 0x10, else a heap pointer at
    // [holder+0x20]), and both its branches end at this setter with the resolved char* in rdx.
    //
    // On 1.5.2 the setter sits 4 bytes past its 1.5.1 address. An offline exe diff
    // (docs/research/PORT_1.5.2_OFFSETS.md) finds the same 11-byte prologue there, once, after the same
    // `e9 01 00 00 00 cc` padding. The 1.5.1 address holds padding on 1.5.2, so a blind hook there would corrupt it.
    private static long FnSetTextString => ImageBase.At(0x1403F109C);

    // Push rbp; push rbx; push rdi; mov rbp,rsp; sub rsp,50h, the same on 1.5.1 and 1.5.2.
    private static readonly byte[] ExpectedPrologue =
        { 0x40, 0x55, 0x53, 0x57, 0x48, 0x8B, 0xEC, 0x48, 0x83, 0xEC, 0x50 };

    // MaxPayloadLen(96) + NUL; PromptSwap.TryPrepareSwap never hands back more than 96 chars.
    private const int PinnedBufLen = 97;

    [Function(CallingConventions.Microsoft)]
    private delegate void SetTextStringFn(nint holder, nint text, nint r8, nint r9);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private readonly PromptSwap _swap;
    private readonly IGameMemory _mem;
    private readonly GCHandle _payloadPin;    // one pinned buffer, never freed (prompts are modal)
    private readonly byte[] _payloadBuf = new byte[PinnedBufLen];
    private IHook<SetTextStringFn>? _hook;
    private SetTextStringFn? _keepalive;      // GC anchor (the native thunk must outlive us)
    private bool _canary;
    private readonly DoorCell _door = new(DoorReading.Waiting("the loader has not handed over its hooks yet"));

    /// <summary>The banners part's door, written by the loader thread and read by the engine tick.</summary>
    public DoorReading Door => _door.Value;

    /// <summary>The loader gave no hooks, so the detour can never arm. An absence, not a failed check.</summary>
    public void MarkNoHooks() => _door.Set(DoorReading.Absent("the loader gave no hooks"));

    public PromptSwapHook(PromptSwap swap, IGameMemory mem)
    {
        _swap = swap;
        _mem = mem;
        _payloadPin = GCHandle.Alloc(_payloadBuf, GCHandleType.Pinned);
    }

    /// <summary>Installs the detour once, called from Engine.InjectHooks. Refuses with one warning when the
    /// read fails or the target lacks ExpectedPrologue, since a hook on a moved function crashes the game.</summary>
    public void Arm(IReloadedHooks hooks)
    {
        if (_hook != null) return;
        bool readOk = _mem.TryReadBytes(FnSetTextString, ExpectedPrologue.Length, out var prologue);
        if (!ShouldArm(readOk, prologue))
        {
            _door.Set(DoorReading.Failed("the game's prompt system is not where this mod expects it"));
            ModLogger.Warn(LogVerb.Toast, PartsCopy.Disabled(
                "Banners (the pop-ups that announce a weapon leveling up) are paused for this session because the game's prompt code is not where this mod expects it; everything else keeps working.",
                "", "", $"target 0x{FnSetTextString:X} " + (readOk ? "prologue mismatch" : "unreadable")));
            return;
        }
        bool live = false;
        try
        {
            _keepalive = Detour;
            _hook = hooks.CreateHook<SetTextStringFn>(_keepalive, FnSetTextString);
            _hook.Activate();
            live = true;
            _door.Set(DoorReading.Open());
            ModLogger.Event(LogVerb.Toast, "Connected to the game's prompt system; tier-up toasts can now be delivered.");
        }
        catch (Exception ex)
        {
            if (!live)
            {
                HookCleanup.DisableQuietly(_hook, LogVerb.Toast, "prompt hook");
                _hook = null;
            }
            _door.Set(DoorReading.Failed("the game's prompt system could not be connected to"));
            // Warn like every other closed door, since the mod keeps running and an error would burn the launch's one flight archive.
            ModLogger.Warn(LogVerb.Toast, PartsCopy.Disabled(
                "Banners (the pop-ups that announce a weapon leveling up) are paused for this session because the mod could not connect to the game's prompt code; everything else keeps working.",
                "", "", ex.Message));
        }
    }

    /// <summary>Install only if the guarded read succeeded and the prologue matches ExpectedPrologue
    /// (HookLandmark.Verify prefix-compare). Separate so PromptSwapHookArmDecisionTests can test it without IReloadedHooks.</summary>
    internal static bool ShouldArm(bool readOk, byte[]? prologue) => readOk && HookLandmark.Verify(prologue, ExpectedPrologue);

    /// <summary>Always forwards (holder, text, r8, r9) to the original, even if TryPrepareSwap faults.</summary>
    private void Detour(nint holder, nint text, nint r8, nint r9)
    {
        if (!_canary)
        {
            _canary = true;
            // Stays on console. Its absence marks a dead-hook launch (Denuvo hook launch fragility).
            ModLogger.EventWithTrace(LogVerb.Toast,
                "The prompt hook is confirmed working; the game's first prompt this session was intercepted.",
                $"prompt-hook canary detail (thread {GetCurrentThreadId()})");
        }
        try
        {
            if (_swap.TryPrepareSwap((long)text, out var payload))
            {
                text = WritePinned(payload);
                ModLogger.EventWithTrace(LogVerb.Toast, $"Delivered the toast \"{payload}\".",
                    $"toast delivery detail (holder=0x{(long)holder:X})");
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Toast, "An error occurred while preparing a toast (the original prompt still displayed normally): " + ex.Message);
        }
        _hook!.OriginalFunction(holder, text, r8, r9);
    }

    /// <summary>ASCII-encodes payload + NUL into the single pinned buffer (the ShowSpike _textPin/_promptPin pattern)
    /// and returns its address. Prompts are modal, so one buffer is enough.</summary>
    private nint WritePinned(string payload)
    {
        Array.Clear(_payloadBuf);
        int n = Math.Min(payload.Length, PinnedBufLen - 1);
        System.Text.Encoding.ASCII.GetBytes(payload, 0, n, _payloadBuf, 0);
        return _payloadPin.AddrOfPinnedObject();
    }
}
