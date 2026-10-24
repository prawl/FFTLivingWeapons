using System;
using System.Threading;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace LivingWeapon;

/// <summary>
/// Detour on the item category getter at <see cref="Offsets.FnCategoryGetter"/>. A new id
/// only shows on the Weapons tab if the getter returns a weapon group code, so extended ids answer
/// as their donor. A managed detour is safe as its callers keep no volatile registers live (as
/// PromptSwapHook); the ExtendedDonorThunks accessors use AccessorEntryHook for the r11 reason.
/// Prologue <c>sub rsp,28h; movzx r11d,cx; mov eax,3FFh</c> read from the 1.5.2 exe.
/// </summary>
internal sealed class CategoryGetterHook
{
    [Function(CallingConventions.Microsoft)]
    public delegate nint GetterFn(nint rcx);

    public static readonly byte[] ExpectedPrologue =
        { 0x48, 0x83, 0xEC, 0x28, 0x44, 0x0F, 0xB7, 0xD9, 0xB8, 0xFF, 0x03, 0x00, 0x00 };

    private readonly ICodePatcher _mem;
    private readonly int _lo;
    private readonly int[] _donors;
    private IHook<GetterFn>? _hook;
    private GetterFn? _keepalive;
    private long _calls, _redirected;

    public long TargetAddr { get; }
    public bool IsActive => _hook?.IsHookEnabled == true;
    public long Calls => Interlocked.Read(ref _calls);
    public long Redirected => Interlocked.Read(ref _redirected);

    /// <param name="lo">First extended id; <paramref name="donors"/>[i] answers for id lo + i.</param>
    public CategoryGetterHook(ICodePatcher mem, int lo, int[] donors, long targetAddr = 0)
    {
        _mem = mem;
        _lo = lo;
        _donors = donors;
        TargetAddr = targetAddr == 0 ? Offsets.FnCategoryGetter : targetAddr;
    }

    /// <summary>The donor to answer as for the raw rcx, or -1 to pass it through untouched.</summary>
    internal static int Resolve(long rcx, int lo, int[] donors)
    {
        int id = (int)(rcx & ThunkStub.IdMask);
        int i = id - lo;
        return i >= 0 && i < donors.Length ? donors[i] : -1;
    }

    internal static bool ShouldArm(bool readOk, byte[]? prologue) => readOk && HookLandmark.Verify(prologue, ExpectedPrologue);

    /// <summary>Null on success, else the refusal. Idempotent.</summary>
    public string? Install(IReloadedHooks hooks)
    {
        if (_hook != null) { if (!_hook.IsHookEnabled) _hook.Enable(); return null; }
        bool ok = _mem.TryRead(TargetAddr, ExpectedPrologue.Length, out var entry);
        if (!ShouldArm(ok, entry))
            return $"category-getter: 0x{TargetAddr:X} does not carry the expected prologue (reads {(ok ? Convert.ToHexString(entry) : "unreadable")})";
        try
        {
            _keepalive = Detour;
            _hook = hooks.CreateHook<GetterFn>(_keepalive, TargetAddr);
            _hook.Activate();
            return null;
        }
        catch (Exception ex) { Release(); _hook = null; return $"category-getter: hook install failed ({ex.Message})"; }
    }

    public void Release()
    {
        try { if (_hook?.IsHookEnabled == true) _hook.Disable(); }
        catch (Exception) { /* swallowed: release must never throw */ }
    }

    // Hot path. No logging or allocation, never throws.
    private nint Detour(nint rcx)
    {
        Interlocked.Increment(ref _calls);
        int donor = Resolve((long)rcx, _lo, _donors);
        if (donor < 0) return _hook!.OriginalFunction(rcx);
        Interlocked.Increment(ref _redirected);
        return _hook!.OriginalFunction((nint)donor);
    }
}
