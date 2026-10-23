using System;
using System.Runtime.InteropServices;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>LW-346 S2 (R12 2026-10-16: entry hooks): the accessor stubs and the entry trampolines, pinned byte for byte AND executed for real. The
/// execution tests allocate an executable page in this test process, plant a three-byte
/// "identity" routine (<c>mov eax,ecx; ret</c>) as the pretend original target, write the stub
/// in front of it, and call the stub through a function pointer: the only way to know the hand
/// assembled bytes do what the comments say, short of the game itself.</summary>
public class ThunkStubTests
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate long NativeFn(long rcx);

    private const long Target = 0x14FE85562L;   // the Denuvo build's weapon-stat thunk target (historic), any value works

    [Fact]
    public void Donor_stub_bytes_are_pinned_exactly()
    {
        var s = ThunkStub.EmitDonorStub(261, new[] { 37 }, Target);
        var expected = new byte[]
        {
            0x89, 0xC8,                               // mov eax, ecx
            0x25, 0xFF, 0x03, 0x00, 0x00,             // and eax, 3FFh
            0x3D, 0x05, 0x01, 0x00, 0x00,             // cmp eax, 261
            0x72, 0x16,                               // jb pass (0x24)
            0x3D, 0x05, 0x01, 0x00, 0x00,             // cmp eax, 261
            0x77, 0x0F,                               // ja pass (0x24)
            0x2D, 0x05, 0x01, 0x00, 0x00,             // sub eax, 261
            0x48, 0x8D, 0x0D, 0x11, 0x00, 0x00, 0x00, // lea rcx, [rip+0x11] -> table at 0x32
            0x8B, 0x0C, 0x81,                         // mov ecx, [rcx+rax*4]
            0xFF, 0x25, 0x00, 0x00, 0x00, 0x00,       // jmp [rip+0]
            0x62, 0x55, 0xE8, 0x4F, 0x01, 0x00, 0x00, 0x00,   // target
            0x25, 0x00, 0x00, 0x00,                   // donors[0] = 37
        };
        Assert.Equal(expected, s);
        Assert.Equal(ThunkStub.DonorStubHeader + 4, s.Length);
    }

    [Fact]
    public void Row_stub_bytes_are_pinned_exactly()
    {
        var row = new byte[] { 0x01, 0x8E, 0x01, 0xFF, 0x0F, 0x00, 0x00, 0x00 };
        var s = ThunkStub.EmitRowStub(261, new[] { row }, Target);
        var expected = new byte[]
        {
            0x89, 0xC8,
            0x25, 0xFF, 0x03, 0x00, 0x00,
            0x3D, 0x05, 0x01, 0x00, 0x00,
            0x72, 0x1B,                               // jb pass (0x29)
            0x3D, 0x05, 0x01, 0x00, 0x00,
            0x77, 0x14,                               // ja pass (0x29)
            0x2D, 0x05, 0x01, 0x00, 0x00,             // sub eax, 261
            0x48, 0xC1, 0xE0, 0x03,                   // shl rax, 3
            0x48, 0x8D, 0x0D, 0x13, 0x00, 0x00, 0x00, // lea rcx, [rip+0x13] -> rows at 0x38
            0x48, 0x01, 0xC8,                         // add rax, rcx
            0xC3,                                     // ret
            0xFF, 0x25, 0x00, 0x00, 0x00, 0x00,       // pass: jmp [rip+0]
            0x62, 0x55, 0xE8, 0x4F, 0x01, 0x00, 0x00, 0x00,
            0x00,                                     // pad
            0x01, 0x8E, 0x01, 0xFF, 0x0F, 0x00, 0x00, 0x00,
        };
        Assert.Equal(expected, s);
        Assert.Equal(ThunkStub.RowStubHeader + 8, s.Length);
    }

    [Fact]
    public void Row_stub_rejects_a_row_of_the_wrong_size()
        => Assert.Throws<ArgumentException>(() => ThunkStub.EmitRowStub(261, new[] { new byte[7] }, Target));

    [Fact]
    public void Jmp_decoding_and_encoding_round_trip()
    {
        var thunk = new byte[] { 0xE9, 0xE9, 0xC8, 0xBC, 0x0F };   // the real 0x1402B8C74 bytes on the Denuvo 1.5.2 build (a function entry since the Denuvo-free exe)
        Assert.True(ThunkStub.IsJmpRel32(thunk, 0x1402B8C74L, out long target));
        Assert.Equal(0x14FE85562L, target);
        Assert.False(ThunkStub.IsJmpRel32(new byte[] { 0x48, 0x83, 0xEC, 0x28, 0x44 }, 0x1402890C0L, out _));
        Assert.False(ThunkStub.IsJmpRel32(null, 1, out _));
        var jmp = ThunkStub.EncodeThunkJmp(0x1402B8C74L, 0x150000000L)!;
        Assert.True(ThunkStub.IsJmpRel32(jmp, 0x1402B8C74L, out long back));
        Assert.Equal(0x150000000L, back);
        Assert.Null(ThunkStub.EncodeThunkJmp(0x1402B8C74L, 0x240000000L));   // > 2 GB away
    }

    [Fact]
    public void Donor_stub_executes_redirecting_extended_ids_and_passing_the_rest_through()
    {
        var page = new LiveNearAllocator().Alloc(4096, 0x140000000L);
        var p = new LiveCodePatcher();
        long identity = page + 0x200;
        Assert.True(p.TryWrite(identity, new byte[] { 0x89, 0xC8, 0xC3 }));   // mov eax,ecx; ret
        var stub = ThunkStub.EmitDonorStub(261, new[] { 37, 67, 19 }, identity);
        Assert.True(p.TryWrite(page, stub));
        var fn = Marshal.GetDelegateForFunctionPointer<NativeFn>((nint)page);
        Assert.Equal(37, fn(261));
        Assert.Equal(67, fn(262));
        Assert.Equal(19, fn(263));
        Assert.Equal(37, fn(0x4105));   // flag bits above bit 9 are masked before the lookup
        Assert.Equal(5, fn(5));         // below the range: rcx passes through untouched
        Assert.Equal(260, fn(260));
        Assert.Equal(264, fn(264));     // past the table: passthrough, never an out-of-table read
        Assert.Equal(511, fn(511));
    }

    [Fact]
    public void Row_stub_executes_returning_our_row_pointer_for_extended_ids_only()
    {
        var page = new LiveNearAllocator().Alloc(4096, 0x140000000L);
        var p = new LiveCodePatcher();
        long identity = page + 0x200;
        Assert.True(p.TryWrite(identity, new byte[] { 0x89, 0xC8, 0xC3 }));
        var rowA = new byte[] { 1, 0x8E, 1, 0xFF, 15, 0, 0, 0 };
        var rowB = new byte[] { 2, 0x8E, 1, 0xFF, 28, 20, 0, 0x11 };
        Assert.True(p.TryWrite(page, ThunkStub.EmitRowStub(261, new[] { rowA, rowB }, identity)));
        var fn = Marshal.GetDelegateForFunctionPointer<NativeFn>((nint)page);
        Assert.Equal(page + ThunkStub.RowStubHeader, fn(261));
        Assert.Equal(page + ThunkStub.RowStubHeader + 8, fn(262));
        Assert.True(p.TryRead(fn(262), 8, out var got));
        Assert.Equal(rowB, got);
        Assert.Equal(37, fn(37));       // vanilla id: the original answers (identity here)
        Assert.Equal(263, fn(263));     // past the rows: passthrough
    }

    [Fact]
    public void Entry_trampoline_bytes_are_pinned_for_a_position_independent_entry()
    {
        // RangeIndex shape: mov eax,0FDh stolen at 0x1402B8BCC; return lands at 0x1402B8BD1.
        var t = ThunkStub.EmitEntryTrampoline(0x1402B8BCCL, new byte[] { 0xB8, 0xFD, 0x00, 0x00, 0x00 }, -1);
        Assert.Equal(new byte[]
        {
            0xB8, 0xFD, 0x00, 0x00, 0x00,                                 // stolen mov eax, 0FDh
            0xFF, 0x25, 0x00, 0x00, 0x00, 0x00,                           // jmp [rip+0]
            0xD1, 0x8B, 0x2B, 0x40, 0x01, 0x00, 0x00, 0x00,               // 0x1402B8BD1
        }, t);
    }

    [Fact]
    public void Entry_trampoline_relocates_a_call_rel32()
    {
        // SpritePair shape: sub rsp,28h; call 0x1402B8BCC, stolen at 0x1402B8E60.
        var stolen = new byte[] { 0x48, 0x83, 0xEC, 0x28, 0xE8, 0x63, 0xFD, 0xFF, 0xFF };
        var t = ThunkStub.EmitEntryTrampoline(0x1402B8E60L, stolen, 4);
        Assert.Equal(new byte[]
        {
            0x48, 0x83, 0xEC, 0x28,                                       // stolen sub rsp,28h
            0xFF, 0x15, 0x02, 0x00, 0x00, 0x00,                           // call [rip+2]
            0xEB, 0x08,                                                   // jmp +8 (over the address)
            0xCC, 0x8B, 0x2B, 0x40, 0x01, 0x00, 0x00, 0x00,               // callTarget 0x1402B8BCC
            0xFF, 0x25, 0x00, 0x00, 0x00, 0x00,                           // jmp [rip+0]
            0x69, 0x8E, 0x2B, 0x40, 0x01, 0x00, 0x00, 0x00,               // return 0x1402B8E69
        }, t);
    }

    [Theory]
    [InlineData(4, -1)]   // shorter than a five-byte jmp
    [InlineData(9, 6)]    // call would run past the stolen bytes
    [InlineData(9, 4)]    // stolen[4] is not 0xE8
    public void Entry_trampoline_rejects_a_short_entry_and_a_misplaced_call(int length, int callAt)
    {
        var stolen = new byte[length];
        for (int i = 0; i < length; i++) stolen[i] = 0x90;
        if (length == 9 && callAt == 6) stolen[6] = 0xE8;   // the opcode is right, the room is not
        Assert.Throws<ArgumentException>(() => ThunkStub.EmitEntryTrampoline(0x1402B8E60L, stolen, callAt));
    }

    [Fact]
    public void Installer_hooks_a_real_entry_and_restores_it()
    {
        var fake = new FakeCodePatcher();
        var alloc = new FakeNearAllocator();
        var entry = ExtendedDonorThunks.WeaponStat;
        fake.Seed(entry.Addr, entry.Expected);
        var hook = new AccessorEntryHook(entry);
        long seenTarget = 0;
        int emits = 0;
        Assert.Null(hook.Install(fake, alloc, t => { seenTarget = t; emits++; return ThunkStub.EmitDonorStub(261, new[] { 67 }, t); }));
        Assert.True(hook.Installed);
        long page = alloc.Requests[0].Got;
        Assert.Single(alloc.Requests);
        Assert.Equal(AccessorEntryHook.PageSize, alloc.Requests[0].Size);
        Assert.Equal(entry.Addr, alloc.Requests[0].Near);
        Assert.Equal(page, seenTarget);                                   // the pass target IS the trampoline
        Assert.Equal(page, hook.TrampolineAddr);
        Assert.Equal(page + AccessorEntryHook.StubOffset, hook.StubAddr);
        Assert.Equal(3, fake.Writes.Count);
        Assert.Equal(page, fake.Writes[0].Addr);                          // trampoline first...
        Assert.Equal(ThunkStub.EmitEntryTrampoline(entry.Addr, entry.Expected, entry.CallAt), fake.Writes[0].Data);
        Assert.Equal(hook.StubAddr, fake.Writes[1].Addr);                 // ...then the stub...
        Assert.Equal(entry.Addr, fake.Writes[2].Addr);                    // ...then the entry
        var now = fake.Read(entry.Addr, entry.Expected.Length);
        Assert.True(ThunkStub.IsJmpRel32(now, entry.Addr, out long jumpsTo));
        Assert.Equal(hook.StubAddr, jumpsTo);
        Assert.Equal(new byte[] { 0x90, 0x90, 0x90 }, now[5..]);
        Assert.Null(hook.Install(fake, alloc, _ => throw new InvalidOperationException("must not re-emit")));   // idempotent
        Assert.Equal(1, emits);
        Assert.True(hook.Restore(fake));
        Assert.False(hook.Installed);
        Assert.Equal(entry.Expected, fake.Read(entry.Addr, entry.Expected.Length));
    }

    [Fact]
    public void SwingId_fallback_stub_bytes_are_pinned_exactly()
    {
        var s = ThunkStub.EmitSwingIdFallbackStub(0x1407B077AL, 0x141853D00L, 261, 267, 0x14028207AL);
        var expected = new byte[]
        {
            0x48, 0xB8, 0x7A, 0x07, 0x7B, 0x40, 0x01, 0x00, 0x00, 0x00,   // mov rax, 0x1407B077A
            0x0F, 0xB7, 0x00,                                             // movzx eax, word [rax]
            0x66, 0x85, 0xC0,                                             // test ax, ax
            0x75, 0x20,                                                   // jnz +0x20 -> 0x32
            0x48, 0xB8, 0x00, 0x3D, 0x85, 0x41, 0x01, 0x00, 0x00, 0x00,   // mov rax, 0x141853D00
            0x0F, 0xB7, 0x04, 0x30,                                       // movzx eax, word [rax+rsi]
            0x3D, 0x05, 0x01, 0x00, 0x00,                                 // cmp eax, 261
            0x72, 0x09,                                                   // jb +9 -> 0x30
            0x3D, 0x0B, 0x01, 0x00, 0x00,                                 // cmp eax, 267
            0x77, 0x02,                                                   // ja +2 -> 0x30
            0xEB, 0x02,                                                   // jmp +2 -> 0x32
            0x31, 0xC0,                                                   // zero: xor eax, eax
            0xFF, 0x25, 0x00, 0x00, 0x00, 0x00,                           // done: jmp [rip+0]
            0x7A, 0x20, 0x28, 0x40, 0x01, 0x00, 0x00, 0x00,               // return: 0x14028207A
        };
        Assert.Equal(expected, s);
        Assert.Equal(ThunkStub.SwingIdStubSize, s.Length);
    }

    [Fact]
    public void SwingId_fallback_stub_rejects_a_bad_range()
    {
        Assert.Throws<ArgumentException>(() => ThunkStub.EmitSwingIdFallbackStub(0x1407B077AL, 0x141853D00L, 267, 261, 0x14028207AL));   // lo > hi
        Assert.Throws<ArgumentException>(() => ThunkStub.EmitSwingIdFallbackStub(0x1407B077AL, 0x141853D00L, 261, 0x400, 0x14028207AL));   // hi > 0x3FF
    }

    /// <summary>LW-365 fix round: the rip-relative disp32 baked into
    /// <see cref="SwingIdFallbackHook.ExpectedSite"/> (<c>00 E7 52 00</c> = 0x52E700) must agree with
    /// <see cref="Offsets.SwingIdWord"/>'s offset from the instruction right after the movzx
    /// (SiteAddr + 7); a drift between the two constants would make the stub read the wrong word
    /// with every other test still green. Pure constant pin: GREEN immediately, no RED phase (see
    /// the fix-round report).</summary>
    [Fact]
    public void SwingId_constants_agree_with_the_site_encoding()
    {
        Assert.Equal(Offsets.FnSwingPrepIdCopy + 7 + 0x52E700, Offsets.SwingIdWord);
        Assert.Equal(0x141853D00L, Offsets.BattleUnitsBase + Offsets.CWeapon);
    }

    [Fact]
    public void Installer_refuses_a_wrong_entry_an_unreadable_entry_and_a_failed_allocation_without_writing()
    {
        var fake = new FakeCodePatcher();
        var alloc = new FakeNearAllocator();
        Func<long, byte[]> emit = t => ThunkStub.EmitDonorStub(261, new[] { 37 }, t);

        var validity = ExtendedDonorThunks.HookedDonorEntries[0].Entry;
        var unreadable = new AccessorEntryHook(validity);
        Assert.Contains("unreadable", unreadable.Install(fake, alloc, emit));

        // The wrong-world case: the eight bytes the old Denuvo exe carries at WeaponStat (an E9
        // thunk plus the following function's bytes). All eight are seeded because the fake
        // refuses any unseeded byte, which would read as "unreadable" instead.
        var stat = ExtendedDonorThunks.WeaponStat;
        fake.Seed(stat.Addr, 0xE9, 0xE9, 0xC8, 0xBC, 0x0F, 0x9A, 0xF1, 0x76);
        var wrong = new AccessorEntryHook(stat);
        Assert.Contains("does not carry the expected bytes", wrong.Install(fake, alloc, emit));

        var probe = ExtendedDonorThunks.HookedDonorEntries[1].Entry;
        fake.Seed(probe.Addr, probe.Expected);
        var noPage = new AccessorEntryHook(probe);
        Assert.Contains("no executable page", noPage.Install(fake, new FakeNearAllocator { RefuseAfter = 0 }, emit));
        Assert.Empty(fake.Writes);

        fake.RefuseWritesAt.Add(probe.Addr);
        var refusedEntry = new AccessorEntryHook(probe);
        Assert.Contains("entry write refused", refusedEntry.Install(fake, alloc, emit));
        Assert.False(refusedEntry.Installed);
        Assert.Equal(2, fake.Writes.Count);   // only the (harmless) trampoline and stub were written
    }

    [Fact]
    public void Installer_refuses_a_stub_that_does_not_fit_without_writing()
    {
        var fake = new FakeCodePatcher();
        var entry = ExtendedDonorThunks.WeaponStat;
        fake.Seed(entry.Addr, entry.Expected);
        var hook = new AccessorEntryHook(entry);
        var why = hook.Install(fake, new FakeNearAllocator(), _ => new byte[AccessorEntryHook.PageSize]);
        Assert.Contains("does not fit in the page", why);
        Assert.Empty(fake.Writes);
        Assert.False(hook.Installed);
    }

    // LW-388 R12 load-bearing execution tests: a pretend accessor in real executable memory,
    // hooked through the real LiveCodePatcher, then called natively.
    [Fact]
    public void Entry_hook_runs_the_stolen_call_and_returns_into_the_function()
    {
        // F: sub rsp,28h; call G; add rsp,28h; ret    G: mov eax,ecx; ret
        var a = new LiveNearAllocator();
        long page = a.Alloc(4096, 0x140000000L);
        var p = new LiveCodePatcher();
        long f = page + 0x800;
        long g = page + 0x900;
        int rel = (int)(g - (f + 4 + 5));
        var body = new byte[] { 0x48, 0x83, 0xEC, 0x28, 0xE8, 0, 0, 0, 0, 0x48, 0x83, 0xC4, 0x28, 0xC3 };
        Array.Copy(BitConverter.GetBytes(rel), 0, body, 5, 4);
        Assert.True(p.TryWrite(f, body));
        Assert.True(p.TryWrite(g, new byte[] { 0x89, 0xC8, 0xC3 }));
        var entry = new AccessorEntry(f, "fake", body[..9], 4);

        var hook = new AccessorEntryHook(entry);
        Assert.Null(hook.Install(p, a, t => ThunkStub.EmitDonorStub(261, new[] { 37 }, t)));
        var fn = Marshal.GetDelegateForFunctionPointer<NativeFn>((nint)f);
        Assert.Equal(37, fn(261));
        Assert.Equal(5, fn(5));
        Assert.Equal(300, fn(300));
        Assert.True(hook.Restore(p));
        Assert.Equal(261, fn(261));
    }

    [Fact]
    public void Entry_hook_runs_a_position_independent_entry()
    {
        // F: mov r8d,5; mov eax,ecx; add eax,r8d; ret
        var a = new LiveNearAllocator();
        long page = a.Alloc(4096, 0x140000000L);
        var p = new LiveCodePatcher();
        long f = page + 0x800;
        var body = new byte[] { 0x41, 0xB8, 0x05, 0x00, 0x00, 0x00, 0x89, 0xC8, 0x44, 0x01, 0xC0, 0xC3 };
        Assert.True(p.TryWrite(f, body));
        var hook = new AccessorEntryHook(new AccessorEntry(f, "fake", body[..6], -1));
        Assert.Null(hook.Install(p, a, t => ThunkStub.EmitDonorStub(261, new[] { 37 }, t)));
        var fn = Marshal.GetDelegateForFunctionPointer<NativeFn>((nint)f);
        Assert.Equal(42, fn(261));
        Assert.Equal(15, fn(10));
    }
}
