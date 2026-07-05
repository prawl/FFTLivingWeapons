#if LWDEV
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace LivingWeapon;

/// <summary>
/// DEV-ONLY spike (BannerSpike tradition, uncommitted): the CHASE instrument that proved the
/// production callout piggyback live before it was productionized (that delivery stack has since been retired for the PromptSwap facing-prompt slot).
/// Evidence trail (docs/research/CALLOUT_BANNER_JOURNEY.md has the full RE writeup):
///  - v4 (20:03): wrapper fires REACH the orchestrator with byte-identical fresh-capture args
///    and draw nothing while natural calls render -- the gap is CONTEXT, not arguments.
///  - v4 mapped the natural shape: one player action = TWO orchestrator calls (type=0 tokens
///    id 0x5062 = ability-NAME bubble on objB; then type=1 id 0x508C = parameter popup on
///    objA with the "ParameterUp" track). The two streams differ ONLY at the id (bytes 12-13).
///  - v5 (20:15): THREADS DIFFER -- natural orchestrator calls run on whatever worker the job
///    scheduler hands the callout job (a different thread every session) while our cold fires
///    ran on a different worker. And the post-ritual snapshots (bound widget/w34/obj state) are
///    IDENTICAL between natural draws and our silent fires -- the ritual completes; pixels don't
///    follow.
///  - v5's chase fired on the RIGHT worker in the natural context, but replayed the SAME
///    tokens 1ms after the natural call -- even a successful render was an invisible
///    duplicate. Design blunder, fixed here.
///
/// v6's two discriminating experiments both make the answer VISIBLE by swapping content:
///  - CHASE: right after a natural call returns (mid-job, action hot), re-fire the
///    ritual with the SAME stream but the id bytes PATCHED to the other capture's id
///    (0x5062 <-> 0x508C). Mechanism works from natural context => the bubble visibly
///    changes to the wrong string.
///  - MUTATE: patch the id inside the natural call's OWN stack buffer BEFORE letting it run
///    -- 100% natural everything, our content. If even this leaves the on-screen text
///    unchanged, the rendered string does NOT come from these tokens and the real show is
///    driven elsewhere -- a decisive fork either way.
///
/// v6 RESULTS (Patrick eyewitness, 21:08 chase-ONLY run): our injected call RENDERS from
/// inside the natural call's own job context -- the raw 0x508C template (with its unbound
/// lstr1/lnum2/lnum4 placeholders) drew in the bubble while the natural call ran unpatched.
/// Cold fires from outside that context never draw. Rung 3 cracked; tokens = template id at
/// +0xC + binding tokens.
///
/// v7 = the last rung: CUSTOM TEXT. Chase now fires the ritual then immediately calls
/// SetTextStringAndCommit 0x14028F720(holder, char*) -- the BUBBALUVS-proven text swap --
/// synchronously on the same thread. Graded outcome built in: our custom string = COMPLETE;
/// raw template = ritual ok but the commit didn't take; nothing = regression.
///
/// v7 RESULT (Patrick eyewitness 21:36): ritual + Commit rendered "BUBBA LUVS THE
/// ORCHESTRATOR" -- custom text through the full native show, piggybacked. The remaining
/// delta at the time: TRUE on-command (no natural callout needed).
///
/// v8 = the anytime portal, RETIRED. Thread-sampling (tools/probes/thread_sample.py) found the
/// logic thread's per-frame pump slot at 0x1402F3899 (a NULL global callback invoked once per
/// logic frame). A managed thunk was planted there to fire cold requests at an arbitrary time.
/// RESULT: the callback fired, but on a sibling worker rather than the action job, and drew
/// NOTHING -- confirming the render gate is JOB CONTEXT (the natural call's own execution
/// context), not thread identity or frame timing (docs/research/CALLOUT_BANNER_JOURNEY.md, "v8" +
/// "THE WALL, precisely"). A cold portal outside that context is a dead end, so it was removed
/// entirely -- the plant was also an always-on ambient write on every dev launch (live before
/// F6 was ever pressed), worth deleting even absent the negative result
/// (handsfree-capbreak-autoarm precedent: ambient auto-arming is a trap).
///
/// ShowSpike is now the CHASE instrument only: F8 arms a re-fire (ritual + custom-text commit)
/// right after the NEXT natural callout returns, in that call's own job context -- the only
/// context proven to render (v6/v7). This is the mechanism the retired piggyback delivery ran
/// in production; ShowSpike stays as the manually-triggered dev proof/regression instrument.
///
/// v9 = the COLD SPAWN/DISMISS experiment (the amended-AC arc). The 2026-07-03 stack-scrape +
/// disasm sweep (docs/research/CALLOUT_BANNER_JOURNEY.md "Track B dig part 2") mapped the native show:
/// ShowBubbleCallout 0x1400EF494 runs dismiss-if-lingering -> arm [ctrl+0xE8]=0x78 ->
/// orchestrator -> helper(ctrl,-2) -> helper(ctrl,0), where helper = 0x140111B74 (VM springboard
/// jmp 0x14A20C367 -- the SAME VM body whose instruction 0x14A20C3B6 the CE write-bp caught
/// SETTING the show flag [ctrl+0xE0]=1). v8's cold fire ran the orchestrator ONLY -- the
/// hypothesis v9 tests is that the missing helper bracket, not fiber context, is why it never
/// drew. F4 replays the full native order from the loop thread (the v8-style wrong context, on
/// purpose -- if it draws, context was never the gate). F9 fires the native teardown alone.
/// Probe fact (callout_timer_probe.py, 03:0x this session): type-0 name bubbles never touch
/// [ctrl+0xE8] or the mgr selector block -- the +0xE8 arm below is cargo-culted from the
/// 0x1400EF494 flavor and harmless either way.
///
/// v9 RESULT (Patrick eyewitness 2026-07-03 03:17, x4): F4 DRAWS from the loop thread -- flag
/// 0->1 on four fires across four different pool threads, "BUBBA LUVS SPAWNING AT WILL" on screen
/// with no natural callout in flight. The context wall is falsified; the helper bracket was the
/// missing visibility half. Post-fire [ctrl+0xE8] decrements (120->119 observed): the engine runs
/// its own countdown on OUR bubble, so duration = arm a bigger value. F9 never registered (eaten
/// key like F3/F7), so DISMISS moved to F6 (proven key; the stack-scrape recon it carried is done
/// -- relegated to F5, which may itself be eaten, acceptable for a finished instrument).
///
/// v10 = the COMMIT TAP (facing-prompt hunt, 2026-07-03). Design pivot (Patrick): deliver toasts
/// by REPLACING the Wait-state facing prompt ("Select a facing direction and press F to confirm")
/// -- a deterministic, player-held UI slot at the end of every action, which persists until the
/// player confirms facing (reader-controlled duration, zero contention). The prompt's plumbing is
/// UNLOCATED (the ui_contention probe proved it is not the callout-bubble subsystem). F5 arms a
/// 10s tap that logs every text commit through BOTH known setters -- SetTextStringAndCommit
/// 0x14028F720 (ANSI) and SetTextString 0x14028F79C (the orchestrator's token-string setter) --
/// with holder + text preview + thread. If the facing prompt rides either, we get its holder AND
/// its writer in one session; interception = swap the string in-flight when a toast is pending.
///
/// v10 RESULT (tap run 2, 06:00): the facing prompt rides SetTextString 0x14028F79C --
/// holder 0x436B017118 (inside the [parent+0x58] sibling subsystem, id 0xBE7, = the
/// ui_contention probe's child58 0x436B017000 + 0x118), text = plain ASCII with markup tags
/// ("Select a facing direction and press <keyicon=ok>"), game thread. Interception = swap the
/// rdx string in the hook when the text prefix matches AND a toast is pending; pass-through
/// otherwise (Patrick's design: broadcast in the Wait-state slot, vanilla fallback).
///
/// v11 = the PROMPT SWAP spike: F8 arms a ONE-SHOT swap of the next facing prompt's text with
/// a pinned test string. Proves: the swap renders, the markup-free ANSI format is accepted, and
/// the prompt's stays-until-confirm lifecycle carries our text. (F8's old CHASE experiment
/// verified the now-retired piggyback -- repurposed.)
///
/// PromptSwap.cs/PromptSwapHook.cs productionized this mechanism (BannerToast queue + always-on
/// swap, no F6 arm needed). Production's SetTextString detour and this spike's F6/F5 taps now
/// STACK on 0x14028F79C (standard Reloaded hook-stacking) -- production wins the race over this
/// spike's swap only if its hook installs innermost, a dev-build curiosity with no correctness
/// impact either way.
///
/// Keys: F4 = COLD SPAWN (full native sequence, loop thread). F6 = arm PROMPT SWAP (one-shot;
/// DISMISS proved out and surrendered the key -- ColdDismiss stays reachable, unbound). F5 =
/// COMMIT TAP for 15s (deduped; the stack-capture recon F5 used to carry is COMPLETE and no
/// longer key-bound). Eaten/dead keys on this box: F3/F7/F9 (LL-hook eaten) and F8 (stopped
/// registering 2026-07-03; was fine the day before) -- MUTATE and CHASE are reachable code but
/// not key-bound today. Working set: F2/F4/F5/F6. (A 2026-07-05 "F2 is dead" verdict was
/// RETRACTED same day: the probe bound to it was never ticked out of battle -- call-site gate,
/// not a dead key.) NOTE: F6 is now SHARED with FlavorSpike's P4 probe (deliberate collision --
/// in battle one press arms the prompt swap AND fires the flavor overwrite).
///
/// Canary discipline (docs/LIVE_LEDGER + denuvo-hook fragility): each detour logs its own
/// first-entry ALIVE line. A natural callout bubble WITHOUT an "orch CALLED" log line =
/// dead hook launch -- restart, never trust silence.
/// </summary>
internal sealed class ShowSpike
{
    private const long FnBannerUpdate = 0x140409A88;   // rcx = banner obj, xmm1 = dt
    private const long FnOrchestrator = 0x140111D20;   // rcx = ctrl, rdx = token obj, r8d = has-text
    private const long FnCalloutWrapper = 0x1400E8A84; // rcx ignored, rdx = token obj, r8d = has-text
    private const int TokenLen = 0x60;                 // capture window (matches the CE capture)

    private enum FireMode { ArmChase = 1, ArmMutate = 2 }
    private const int IdOffset = 0xC;   // string-table id within the token stream (bytes 12-13)

    [Function(CallingConventions.Microsoft)]
    private delegate void UpdateFn(nint bannerObj, float dt);

    [Function(CallingConventions.Microsoft)]
    private delegate void OrchFn(nint ctrl, nint textObj, nint hasText, nint r9);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WrapperFn(nint unused, nint textObj, int hasText);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void SetTextCommitFn(nint holder, nint ansiStr);
    private const long FnSetTextCommit = 0x14028F720;  // the BUBBALUVS-proven set-text + flush
    private const string ChaseText = "BUBBA LUVS THE ORCHESTRATOR";

    // v10 commit tap: both known text setters, hooked with 4-arg forwarding (r8/r9 verbatim --
    // the OrchFn precedent; the true arity is unproven, forwarding extra registers is free).
    private const long FnSetTextString = 0x14028F79C;  // the orchestrator's token-string setter
    private const int TapWindowSeconds = 15;
    private const int TapMaxLines = 60;                // UNIQUE (setter,holder,text) entries per window
                                                       // -- v10 first run: a name-plate re-commits at
                                                       // 60Hz and flooded a raw per-line cap

    [Function(CallingConventions.Microsoft)]
    private delegate void TextCommitTapFn(nint holder, nint text, nint r8, nint r9);

    // v11 prompt swap: the facing prompt's leading text (EN; the mod's text layer is EN-only,
    // french-nxd-walled) and the pinned replacement payload.
    private const string FacingPromptPrefix = "Select a facing";
    private const string PromptSwapText = "BUBBA LUVS THE WAIT STATE";

    // v9 cold spawn/dismiss (Track B dig part 2, disasm-verified 2026-07-03): the stream-control
    // helper bracketing every native show. edx = -2 kills the current bubble, edx = 0 is the
    // post-show commit; its VM body (0x14A20C367) contains the show-flag setter 0x14A20C3B6.
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void HelperFn(nint ctrl, int code);
    private const long FnStreamHelper = 0x140111B74;
    private const long GSlot = 0x143CD9DA8;      // static root of the controller chain (dev-only now; production toast delivery is PromptSwap)
    private const long LingerOffset = 0xE8;      // dword countdown ShowBubbleCallout arms to 0x78
    private const long ShowFlagOffset = 0xE0;    // byte the helper's VM body sets to 1 on show
    // Native shows arm 0x78 (120 ticks). F6 proved zeroing it + helper(-2) tears down early, so
    // the countdown IS the lifetime; 0x258 = 5x native, an eyewitness test of lifetime scaling
    // (the observed seconds also derive the tick rate).
    private const uint LingerArmTicks = 0x258;
    private const string SpawnText = "BUBBA LUVS SPAWNING AT WILL";

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    // Frame-pointer walk over the CURRENT thread's stack; safe to call from inside the detour
    // (read-only, no allocation beyond the managed array). Exported by kernel32 (winnt.h).
    [DllImport("kernel32.dll")]
    private static extern ushort RtlCaptureStackBackTrace(uint framesToSkip, uint framesToCapture,
                                                          [Out] IntPtr[] backTrace, out uint hash);
    [DllImport("kernel32.dll")]
    private static extern void GetCurrentThreadStackLimits(out nuint lowLimit, out nuint highLimit);
    private const int VkF8 = 0x77;
    private const int VkF6 = 0x75;   // DISMISS (F9 is eaten on this box; F6 is proven)
    private const int VkF5 = 0x74;   // stack capture (recon complete; eaten-key risk acceptable)
    private const int VkF4 = 0x73;
    private const int StackCapFrames = 62;   // RtlCaptureStackBackTrace's documented safe max

    private readonly IGameMemory _mem;
    private readonly GCHandle _tokenPin;      // pinned 0x100 buffer: tokens + zero tail (over-read guard)
    private readonly GCHandle _textPin;       // pinned ANSI ChaseText + NUL
    private readonly GCHandle _spawnTextPin;  // pinned ANSI SpawnText + NUL (F4's distinct eyewitness string)
    private readonly byte[] _lastTokens = new byte[TokenLen];   // most recent natural capture
    private readonly byte[] _nameTokens = new byte[TokenLen];   // most recent TYPE-0 (name bubble) capture
    private IHook<UpdateFn>? _updateHook;
    private IHook<OrchFn>? _orchHook;
    private IHook<TextCommitTapFn>? _commitTapHook;    // v10: SetTextStringAndCommit 0x14028F720
    private IHook<TextCommitTapFn>? _setTextTapHook;   // v10: SetTextString 0x14028F79C
    private UpdateFn? _updateKeepalive;       // GC anchors: the native thunks must outlive us
    private OrchFn? _orchKeepalive;
    private TextCommitTapFn? _commitTapKeepalive;
    private TextCommitTapFn? _setTextTapKeepalive;
    private bool _updateCanary, _orchCanary, _tapCanary;
    private long _tapUntilTicks;              // DateTime.Ticks; loop thread writes, game threads read
    private int _tapLines;
    private readonly HashSet<string> _tapSeen = new();   // dedupe key: setter:holder:text (locked)
    private volatile bool _promptSwap;        // v11: one-shot facing-prompt text swap armed (F8)
    private readonly GCHandle _promptPin;     // pinned ANSI PromptSwapText + NUL
    private volatile bool _haveLast, _haveName;
    private uint _lastType = 1;
    private volatile bool _firing;            // marks OUR wrapper call inside the orch detour
    private volatile bool _chase;             // re-fire id-patched right after the next natural call
    private volatile bool _mutate;             // patch the id inside the next natural call's buffer
    private volatile int _stackCaps;          // capture the caller chain of the next N natural calls (F6)
    private bool _f6Was;
    private bool _f5Was;
    private bool _f4Was;
    private int _hbTick;

    public ShowSpike(IGameMemory mem)
    {
        _mem = mem;
        _tokenPin = GCHandle.Alloc(new byte[0x100], GCHandleType.Pinned);
        var text = new byte[ChaseText.Length + 1];
        System.Text.Encoding.ASCII.GetBytes(ChaseText).CopyTo(text, 0);
        _textPin = GCHandle.Alloc(text, GCHandleType.Pinned);
        var spawn = new byte[SpawnText.Length + 1];
        System.Text.Encoding.ASCII.GetBytes(SpawnText).CopyTo(spawn, 0);
        _spawnTextPin = GCHandle.Alloc(spawn, GCHandleType.Pinned);
        var prompt = new byte[PromptSwapText.Length + 1];
        System.Text.Encoding.ASCII.GetBytes(PromptSwapText).CopyTo(prompt, 0);
        _promptPin = GCHandle.Alloc(prompt, GCHandleType.Pinned);
    }

    /// <summary>Install both detours. Idempotent; called from Engine.InjectHooks
    /// when Mod.Start/StartEx resolves IReloadedHooks.</summary>
    public void Arm(IReloadedHooks hooks)
    {
        if (_updateHook != null) return;
        try
        {
            _updateKeepalive = UpdateDetour;
            _updateHook = hooks.CreateHook<UpdateFn>(_updateKeepalive, FnBannerUpdate).Activate();
            _orchKeepalive = OrchDetour;
            _orchHook = hooks.CreateHook<OrchFn>(_orchKeepalive, FnOrchestrator).Activate();
            _commitTapKeepalive = (h, t, r8, r9) => TapDetour(_commitTapHook!, "SetTextCommit", h, t, r8, r9);
            _commitTapHook = hooks.CreateHook<TextCommitTapFn>(_commitTapKeepalive, FnSetTextCommit).Activate();
            _setTextTapKeepalive = (h, t, r8, r9) => TapDetour(_setTextTapHook!, "SetTextString", h, t, r8, r9);
            _setTextTapHook = hooks.CreateHook<TextCommitTapFn>(_setTextTapKeepalive, FnSetTextString).Activate();
            ModLogger.Event(LogVerb.Trace, "show-spike: dev research hooks installed (banner-update, orchestrator, text-setter tap); healthy hooks announce themselves with ALIVE lines below; a callout bubble appearing on screen with no orchestrator ALIVE line means the hooks are dead this launch, restart the game");
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Trace, "show-spike: hook install failed; " + ex.Message);
        }
    }

    /// <summary>Loop-thread tick: heartbeat + F8 key edge. The key only sets the pending arm --
    /// game-memory work runs on the game's thread inside the detour.</summary>
    public void Tick()
    {
        if (++_hbTick % 300 == 0)   // ~10s at 33ms
        {
            ModLogger.Debug(LogVerb.Trace, $"show-spike: alive (hooks={(_updateHook != null ? "yes" : "NO")}/{(_orchHook != null ? "yes" : "NO")} canaries={_updateCanary}/{_orchCanary} captures={_haveLast}/{_haveName} chase={_chase})");
        }
        // F8 stopped registering on this box 2026-07-03 (worked 2026-07-02 -- keyboard/hook
        // drift); the swap experiment moved to F6 (DISMISS is proven and gave up its key).
        if (Pressed(VkF6, ref _f6Was))
        {
            _promptSwap = true;
            ModLogger.Event(LogVerb.Trace, $"show-spike: F6; PROMPT SWAP armed (one-shot): the next facing prompt renders \"{PromptSwapText}\" and should STAY until you confirm facing");
        }
        if (Pressed(VkF5, ref _f5Was))
        {
            lock (_tapSeen) _tapSeen.Clear();
            _tapLines = 0;
            System.Threading.Interlocked.Exchange(ref _tapUntilTicks,
                DateTime.Now.AddSeconds(TapWindowSeconds).Ticks);
            ModLogger.Event(LogVerb.Trace, $"show-spike: F5; COMMIT TAP armed {TapWindowSeconds}s, deduped (end an action and sit in the facing prompt; hunting its text writer)");
        }
        // v9: cold spawn runs RIGHT HERE on the loop thread -- the deliberately-wrong context.
        // Spawn PROVEN 2026-07-03 03:17 eyewitness x4; dismiss PROVEN same night ("F6 removes it
        // early") and then gave its key to the v11 swap -- ColdDismiss stays reachable, unbound.
        if (Pressed(VkF4, ref _f4Was))
            ColdSpawn();
    }

    private static bool Pressed(int vk, ref bool was)
    {
        bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
        bool pressed = down && !was;
        was = down;
        return pressed;
    }

    private void Queue(FireMode mode, string what)
    {
        if (_updateHook == null)
        {
            ModLogger.Event(LogVerb.Trace, "show-spike: key pressed but no hooks installed (IReloadedHooks never arrived)");
            return;
        }
        if (mode == FireMode.ArmChase)
        {
            _chase = true;
            ModLogger.Event(LogVerb.Trace, $"show-spike: {what}; CHASE armed: id-patched replay fires right after the NEXT natural callout returns (cast something; watch for the WRONG string in the bubble)");
            return;
        }
        // FireMode.ArmMutate -- unreachable via any key today (F7 is eaten, see the class doc),
        // kept as a live-fire experiment for a box where F7 registers.
        _mutate = true;
        ModLogger.Event(LogVerb.Trace, $"show-spike: {what}; MUTATE armed: the NEXT natural callout's own token id gets patched in-flight (cast something; the natural bubble should show the WRONG string)");
    }

    /// <summary>Post-ritual forensics: what did the orchestrator leave bound/armed? Logged
    /// after EVERY orchestrator return so natural lines diff directly against ours.</summary>
    private void SnapshotAfter(long ctrl, uint typeAtEntry)
    {
        long holder = ctrl + 0x58;
        long w = (long)_mem.U64(holder + 0x10);
        long objSlot = typeAtEntry >= 1 && typeAtEntry <= 6 ? 0x38 : typeAtEntry is 7 or 8 ? 0x48 : 0x40;
        long obj = (long)_mem.U64(ctrl + objSlot);
        string ws = w != 0
            ? $"widget={w:x} w34={_mem.U32(w + 0x34)} w90={_mem.U8(w + 0x90)} wCC={_mem.U8(w + 0xCC)} alpha={_mem.U8(w + 0xC7):x2}"
            : "widget=NULL";
        ModLogger.Debug(LogVerb.Trace, $"show-spike: post-ritual: {ws} obj={obj:x} obj218={(obj != 0 ? _mem.U32(obj + 0x218) : 0)} obj280={(obj != 0 ? _mem.U8(obj + 0x280) : 0):x2} holder88={_mem.U8(holder + 0x88)}");
    }

    /// <summary>Runs at every orchestrator call -- natural or ours. Captures the request
    /// while the caller's stack text object is still live; snapshots after the ritual.</summary>
    private void OrchDetour(nint ctrl, nint textObj, nint hasText, nint r9)
    {
        if (!_orchCanary)
        {
            _orchCanary = true;
            ModLogger.Event(LogVerb.Trace, $"show-spike: orch hook ALIVE (thread {GetCurrentThreadId()})");
        }
        uint type = 0;
        try
        {
            type = _mem.U32((long)ctrl + 0x50);
            string tok = "<none>";
            if (textObj != 0 && hasText != 0 && _mem.TryReadBytes(textObj, TokenLen, out var bytes))
            {
                tok = Convert.ToHexString(bytes);
                if (!_firing)
                {
                    bytes.CopyTo(_lastTokens, 0);
                    _lastType = type;
                    _haveLast = true;
                    if (type == 0)
                    {
                        bytes.CopyTo(_nameTokens, 0);   // F4 wants the NAME-bubble stream, and
                        _haveName = true;               // type-1 overwrites _lastTokens ~1.6s later
                    }
                    if (_mutate)
                    {
                        _mutate = false;
                        var patched = (byte[])bytes.Clone();
                        if (FlipId(patched))
                        {
                            _mem.WriteBytes((long)textObj + IdOffset, new[] { patched[IdOffset], patched[IdOffset + 1] });
                            ModLogger.Event(LogVerb.Trace, $"show-spike: MUTATE; patched the natural request's id in-flight ({bytes[IdOffset + 1]:x2}{bytes[IdOffset]:x2} -> {patched[IdOffset + 1]:x2}{patched[IdOffset]:x2}); does the bubble show the WRONG string?");
                        }
                        else
                        {
                            ModLogger.Event(LogVerb.Trace, "show-spike: MUTATE skipped; unrecognized id in the natural stream");
                        }
                    }
                }
            }
            // Spawn-hunt recon (callout-on-demand arc): the wrapper family has zero direct
            // real-code callers (caller_scan 2026-07-03), so the request-enqueue upstream is
            // only reachable through a live caller chain captured mid-call. Natural calls only.
            if (!_firing && _stackCaps > 0)
            {
                _stackCaps--;
                var frames = new IntPtr[StackCapFrames];
                ushort n = RtlCaptureStackBackTrace(0, StackCapFrames, frames, out _);
                var sb = new System.Text.StringBuilder(n * 12);
                for (int i = 0; i < n; i++) sb.Append(((long)frames[i]).ToString("x")).Append(' ');
                ModLogger.Debug(LogVerb.Trace, $"show-spike: STACK type={type} thread={GetCurrentThreadId()} frames[{n}]: {sb.ToString().TrimEnd()}");
                // The unwind walker dies at the managed/Denuvo boundary (frames[1] observed live
                // 2026-07-03), so ALSO raw-scrape the live stack for return-address-shaped qwords.
                // This derefs OUR OWN thread's stack between a local and the stack base -- committed
                // by definition (we are standing on it), the one sanctioned exception to the
                // no-raw-deref rule (game memory still goes through Mem/IGameMemory).
                unsafe
                {
                    byte anchor;
                    long rsp = (long)&anchor;
                    GetCurrentThreadStackLimits(out _, out nuint highLimit);
                    long span = Math.Min(0x4000, (long)highLimit - rsp);
                    var hitSb = new System.Text.StringBuilder();
                    int hits = 0;
                    for (long a = rsp; a + 8 <= rsp + span && hits < 64; a += 8)
                    {
                        long q = *(long*)a;
                        // Game image + Denuvo VM regions (caller_scan 2026-07-03: 36 exec regions
                        // spanning 0x140000000..~0x14D000000).
                        if (q >= 0x140000000 && q < 0x14D000000)
                        {
                            hitSb.Append('+').Append((a - rsp).ToString("x")).Append(':')
                                 .Append(q.ToString("x")).Append(' ');
                            hits++;
                        }
                    }
                    ModLogger.Debug(LogVerb.Trace, $"show-spike: STACKSCRAPE type={type} span=0x{span:x} hits[{hits}]: {hitSb.ToString().TrimEnd()}");
                }
            }
            ModLogger.Debug(LogVerb.Trace, $"show-spike: orch CALLED{(_firing ? " (OUR replay)" : "")} thread={GetCurrentThreadId()}: ctrl={(long)ctrl:x} type={type} textObj={(long)textObj:x} r8={(long)hasText:x} tokens={tok}");
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Trace, "show-spike: orch detour log failed; " + ex.Message);
        }
        _orchHook!.OriginalFunction(ctrl, textObj, hasText, r9);
        try
        {
            SnapshotAfter(ctrl, type);
            if (_chase && !_firing)
            {
                _chase = false;
                ModLogger.Event(LogVerb.Trace, "show-spike: CHASE firing; ritual + custom-text commit in the natural call's own thread/context");
                Fire((long)ctrl, patchId: true);
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Trace, "show-spike: orch detour post failed; " + ex.Message);
        }
    }

    /// <summary>v10/v11 tap detour, shared by both text setters. While the F5 window is open,
    /// logs unique (holder, text ptr, guarded ASCII preview, native tid) lines; while the F8
    /// one-shot is armed and THIS is a SetTextString carrying the facing prompt, swaps the text
    /// pointer to the pinned test payload. ALWAYS forwards to the original with r8/r9 verbatim.
    /// All text reads go through IGameMemory.TryReadBytes (guarded RPM) -- never a raw deref.</summary>
    private void TapDetour(IHook<TextCommitTapFn> hook, string which, nint holder, nint text, nint r8, nint r9)
    {
        try
        {
            if (!_tapCanary)
            {
                _tapCanary = true;
                ModLogger.Event(LogVerb.Trace, $"show-spike: commit-tap hooks ALIVE (first {which}, thread {GetCurrentThreadId()})");
            }
            bool tapOpen = DateTime.Now.Ticks < System.Threading.Interlocked.Read(ref _tapUntilTicks)
                           && _tapLines <= TapMaxLines;
            bool swapEligible = _promptSwap && which == "SetTextString";
            if (tapOpen || swapEligible)
            {
                string preview = "<unreadable>";
                if (text != 0 && _mem.TryReadBytes(text, 48, out var bytes))
                {
                    var sb = new System.Text.StringBuilder(48);
                    foreach (byte b in bytes)
                    {
                        if (b == 0) break;
                        sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
                    }
                    preview = sb.ToString();
                }
                if (tapOpen)
                {
                    // Dedupe: a HUD name-plate re-commits identical text at 60Hz (first tap run);
                    // only a NEW (setter, holder, text) combination earns a line.
                    bool fresh;
                    lock (_tapSeen) fresh = _tapSeen.Add($"{which}:{(long)holder:X}:{preview}");
                    if (fresh && System.Threading.Interlocked.Increment(ref _tapLines) <= TapMaxLines)
                        ModLogger.Event(LogVerb.Trace, $"show-spike: commit-tap {which} holder=0x{(long)holder:X} text=0x{(long)text:X} \"{preview}\" (tid {GetCurrentThreadId()})");
                }
                if (swapEligible && preview.StartsWith(FacingPromptPrefix, StringComparison.Ordinal))
                {
                    _promptSwap = false;   // one-shot: the next facing prompt is vanilla again
                    text = _promptPin.AddrOfPinnedObject();
                    ModLogger.Event(LogVerb.Trace, $"show-spike: PROMPT SWAP fired; holder=0x{(long)holder:X} now carries \"{PromptSwapText}\" (does it render and STAY until confirm?)");
                }
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Trace, "show-spike: commit-tap log failed; " + ex.Message);
        }
        hook.OriginalFunction(holder, text, r8, r9);
    }

    /// <summary>Runs on the game's RENDER thread, once per banner object per frame. v5 proved
    /// fires from here never draw -- it stays only as a liveness canary.</summary>
    private void UpdateDetour(nint bannerObj, float dt)
    {
        _updateHook!.OriginalFunction(bannerObj, dt);
        if (!_updateCanary)
        {
            _updateCanary = true;
            ModLogger.Event(LogVerb.Trace, $"show-spike: update hook ALIVE (thread {GetCurrentThreadId()})");
        }
    }

    /// <summary>Swap the stream's string-table id between the two ids v4/v5 captured live
    /// (0x5062 = the ability-name string, 0x508C = the parameter string). Returns false on
    /// an unrecognized id so experiments never fire a guessed value.</summary>
    private static bool FlipId(byte[] buf)
    {
        ushort id = (ushort)(buf[IdOffset] | buf[IdOffset + 1] << 8);
        ushort other = id == 0x5062 ? (ushort)0x508C : id == 0x508C ? (ushort)0x5062 : (ushort)0;
        if (other == 0) return false;
        buf[IdOffset] = (byte)(other & 0xFF);
        buf[IdOffset + 1] = (byte)(other >> 8);
        return true;
    }

    /// <summary>Game-thread only, called right after a natural callout returns (CHASE). Stamps
    /// the request type from the last natural capture (caller state -- v3's type=0 lesson: the
    /// wrapper does NOT set it, and an unmatched type walks off the orchestrator's switch),
    /// replays the captured tokens (optionally id-flipped), then commits the custom text.</summary>
    private void Fire(long ctrl, bool patchId = false)
    {
        if (!_haveLast)
        {
            ModLogger.Event(LogVerb.Trace, "show-spike: no capture yet this launch; cast something first, then retry");
            return;
        }
        FireStream(ctrl, (byte[])_lastTokens.Clone(), _lastType, patchId, "CHASE",
                   _textPin.AddrOfPinnedObject(), ChaseText);
    }

    /// <summary>The shared ritual core: stamp type, wrapper-fire the token stream, then the
    /// v7-proven custom-text commit. Caller supplies the stream/type (CHASE = last natural
    /// capture; F4 = the retained type-0 name-bubble capture).</summary>
    private void FireStream(long ctrl, byte[] src, uint type, bool patchId, string mode, nint text, string textLabel)
    {
        try
        {
            if (_mem.U32(ctrl + 0x50) != type)
                _mem.WriteBytes(ctrl + 0x50, BitConverter.GetBytes(type));
            var fire = Marshal.GetDelegateForFunctionPointer<WrapperFn>(unchecked((nint)FnCalloutWrapper));
            _firing = true;
            string patched = patchId ? (FlipId(src) ? " id-PATCHED" : " id-patch SKIPPED (unrecognized id)") : "";
            Marshal.Copy(src, 0, _tokenPin.AddrOfPinnedObject(), TokenLen);
            fire(0, _tokenPin.AddrOfPinnedObject(), 1);
            _firing = false;
            ModLogger.Event(LogVerb.Trace, $"show-spike: wrapper fired (mode={mode}{patched} ctrl={ctrl:x} type={type} thread={GetCurrentThreadId()})");
            // The v7-proven text swap: overwrite whatever the ritual set, synchronously.
            var commit = Marshal.GetDelegateForFunctionPointer<SetTextCommitFn>(unchecked((nint)FnSetTextCommit));
            commit((nint)(ctrl + 0x58), text);
            ModLogger.Event(LogVerb.Trace, $"show-spike: custom text committed after the ritual; does the bubble say '{textLabel}'?");
        }
        catch (Exception ex)
        {
            _firing = false;
            ModLogger.Error(LogVerb.Trace, "show-spike: managed exception in game-thread fire; " + ex.Message);
        }
    }

    /// <summary>Cold-context controller resolve -- the normative FOUR-U64-deref chain
    /// (the journey doc's bracket notation is one deref
    /// short). Returns 0 when any hop is null (not in battle / UI not built yet).</summary>
    private long ResolveCtrl()
    {
        long g = (long)_mem.U64(GSlot);
        long p = g != 0 ? (long)_mem.U64(g + 0x10) : 0;
        long subsys = p != 0 ? (long)_mem.U64(p + 0x48) : 0;
        return subsys != 0 ? (long)_mem.U64(subsys + 0x58) : 0;
    }

    /// <summary>F4: the v9 experiment. Replays the FULL native show order (ShowBubbleCallout
    /// 0x1400EF494, disasm 2026-07-03): dismiss-if-lingering -> arm [ctrl+0xE8]=0x78 -> ritual
    /// (orchestrator + custom text) -> helper(-2) -> helper(0) -- from the loop thread, i.e. the
    /// context v8 PROVED never draws with the orchestrator alone. Any bubble appearing here =
    /// on-demand spawn cracked (even with the wrong text). Crash risk accepted: first cold call
    /// of a VM function; a dead game = restart, the standard spike tax.</summary>
    private void ColdSpawn()
    {
        long ctrl = ResolveCtrl();
        if (ctrl == 0)
        {
            ModLogger.Event(LogVerb.Trace, "show-spike: F4; controller chain unresolved (enter a battle first)");
            return;
        }
        if (!_haveName)
        {
            ModLogger.Event(LogVerb.Trace, "show-spike: F4; no type-0 capture yet this launch (do one attack first, then retry)");
            return;
        }
        try
        {
            uint timer = _mem.U32(ctrl + LingerOffset);
            ModLogger.Event(LogVerb.Trace, $"show-spike: F4 COLD SPAWN (loop thread {GetCurrentThreadId()}); pre: flag={_mem.U8(ctrl + ShowFlagOffset)} timer={timer}");
            var helper = Marshal.GetDelegateForFunctionPointer<HelperFn>(unchecked((nint)FnStreamHelper));
            if (timer > 0)
            {
                _mem.WriteBytes(ctrl + LingerOffset, BitConverter.GetBytes(0u));
                helper((nint)ctrl, -2);   // native pre-dismiss of a lingering show
            }
            _mem.WriteBytes(ctrl + LingerOffset, BitConverter.GetBytes(LingerArmTicks));
            FireStream(ctrl, (byte[])_nameTokens.Clone(), 0, patchId: false, "F4-COLD",
                       _spawnTextPin.AddrOfPinnedObject(), SpawnText);
            helper((nint)ctrl, -2);
            helper((nint)ctrl, 0);
            ModLogger.Event(LogVerb.Trace, $"show-spike: F4 sequence complete; post: flag={_mem.U8(ctrl + ShowFlagOffset)} timer={_mem.U32(ctrl + LingerOffset)}; IS THERE A BUBBLE?");
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Trace, "show-spike: F4 cold spawn failed; " + ex.Message);
        }
    }

    /// <summary>F6: the native teardown alone -- helper(ctrl, -2) (+ zero the linger dword when
    /// armed, mirroring ShowBubbleCallout's own pre-dismiss). Press while a bubble is showing
    /// (natural or F4-spawned); an instant vanish = on-demand dismiss proven.</summary>
    private void ColdDismiss()
    {
        long ctrl = ResolveCtrl();
        if (ctrl == 0)
        {
            ModLogger.Event(LogVerb.Trace, "show-spike: DISMISS; controller chain unresolved (enter a battle first)");
            return;
        }
        try
        {
            byte flag = _mem.U8(ctrl + ShowFlagOffset);
            uint timer = _mem.U32(ctrl + LingerOffset);
            if (timer > 0)
                _mem.WriteBytes(ctrl + LingerOffset, BitConverter.GetBytes(0u));
            var helper = Marshal.GetDelegateForFunctionPointer<HelperFn>(unchecked((nint)FnStreamHelper));
            helper((nint)ctrl, -2);
            ModLogger.Event(LogVerb.Trace, $"show-spike: DISMISS fired; pre flag={flag} timer={timer}, post flag={_mem.U8(ctrl + ShowFlagOffset)}; did the bubble vanish?");
        }
        catch (Exception ex)
        {
            ModLogger.Error(LogVerb.Trace, "show-spike: DISMISS failed; " + ex.Message);
        }
    }
}
#endif
