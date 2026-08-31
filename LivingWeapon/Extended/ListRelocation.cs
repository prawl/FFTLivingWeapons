using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-368 round 2: lifts the extended inventory's ceiling past eleven items. Plain language: the
/// game keeps "how many of each item you own" as one byte per item in a fixed block that ends
/// where Ramza's own roster row begins, plus a second per-item byte list right beside it; a new
/// item id past that edge reads and writes into Ramza's own save data instead of its own count.
/// This class copies both lists onto a page the mod owns and re-points the 55 places in the
/// game's code that name the old blocks (the table itself lives in ListRelocation.Sites.cs, the
/// WHAT half of this partial), so every id up to <see cref="ExtendedSites.MaxExtendedCount"/>
/// gets its own byte instead of borrowing Ramza's.
///
/// Same posture as its closest sibling, <see cref="ShopFlagsMirror"/>: every field is read back
/// and must still carry its VANILLA value before a single byte is written (a mismatch means
/// another patch, or a previous session's leaked page, got there first), and a refusal midway
/// restores every field this call already changed, in reverse, before it returns. Two
/// differences from that sibling: this is a MOVE, not a mirror (D1: every known reader/writer is
/// re-pointed, and the old blocks go dead rather than staying in sync), and there is no live
/// resync step, because nothing needs the old blocks to keep working once every reference to them
/// is gone. <see cref="CheckOldBlocks"/> is the safety net for that assumption: it remembers what
/// the two old blocks looked like the moment they were copied, and answers once, ever, the first
/// time either one changes afterwards -- proof some reference this sweep missed is still writing
/// there. Restore only ever puts the 55 fields back (D5): a mid-session copy-back would race
/// whatever is running on the game's own thread, so the page itself is leaked by design, the same
/// "never free a code/data page" rule <see cref="INearAllocator"/> already keeps.
///
/// Every memory access goes through the injected <see cref="ICodePatcher"/> (VirtualProtect +
/// RPM/WPM guarded, never a raw pointer deref) and <see cref="INearAllocator"/> (never frees).
/// </summary>
internal sealed partial class ListRelocation
{
    // volatile (the Mem.WritesEnabled idiom): Install runs on the boot thread before the game's
    // hook threads exist (P12), but ExtendedInventory.BagCountBase reads this from whichever
    // thread calls it once armed, so the flag needs release/acquire semantics -- written LAST,
    // after every other field below, so a true read here guarantees CountBase already published.
    private volatile bool _installed;

    /// <summary>True once every field points at the new page.</summary>
    public bool Installed => _installed;

    /// <summary>The page the two lists live on once installed; 0 before that.</summary>
    public long PageAddr { get; private set; }

    /// <summary>Where the bag counts resolve right now: the new page's count copy once
    /// installed, else <see cref="Offsets.BagCountArray"/> (the vanilla block).</summary>
    public long CountBase { get; private set; } = Offsets.BagCountArray;

    /// <summary>Where the sibling flag list resolves right now: the new page's copy once
    /// installed, else <see cref="Offsets.SiblingListArray"/> (the vanilla block).</summary>
    public long SiblingBase { get; private set; } = Offsets.SiblingListArray;

    private byte[]? _oldCountSnapshot, _oldSiblingSnapshot;
    private bool _warnedOnce;

    /// <summary>Null on success, else the refusal (nothing changed). Idempotent once installed.
    /// Order (D3, T6): every one of the 55 fields must already read vanilla, THEN the page is
    /// allocated and zero-filled, THEN the two lists are copied onto it, and only THEN does the
    /// first field get rewritten -- so a refusal at any step before the last leaves every byte in
    /// the process exactly as it was found.</summary>
    public string? Install(ICodePatcher patcher, INearAllocator allocator)
    {
        if (Installed) return null;
        foreach (var site in Sites)
        {
            if (!patcher.TryRead(site.Addr, 4, out var bytes))
                return $"list-relocation: 0x{site.Addr:X} is unreadable (expected vanilla 0x{site.Vanilla:X8})";
            int now = BitConverter.ToInt32(bytes, 0);
            if (now != site.Vanilla)
                return $"list-relocation: 0x{site.Addr:X} reads 0x{now:X8}, expected vanilla 0x{site.Vanilla:X8} (already redirected or moved)";
        }

        long page = allocator.Alloc(PageSize, Offsets.ModuleBase);
        if (page == 0) return "list-relocation: no page within reach of the image base";
        string? reach = CheckReach(page);
        if (reach != null) return reach;
        if (!patcher.TryWrite(page, new byte[PageSize])) return $"list-relocation: zero-fill refused at 0x{page:X}";

        if (!SnapshotOldBlocks(patcher, out var countBlock, out var siblingBlock))
            return "list-relocation: an old list block is unreadable";
        if (!patcher.TryWrite(page, countBlock)) return "list-relocation: the count-list copy was refused";
        if (!patcher.TryWrite(page + SiblingPageOffset, siblingBlock)) return "list-relocation: the sibling-list copy was refused";

        var written = new List<Site>(Sites.Length);
        foreach (var site in Sites)
        {
            if (!patcher.TryWrite(site.Addr, Encode(NewField(site, page))))
            {
                for (int i = written.Count - 1; i >= 0; i--)
                    patcher.TryWrite(written[i].Addr, Encode(written[i].Vanilla));
                return $"list-relocation: field write refused at 0x{site.Addr:X}";
            }
            written.Add(site);
        }

        PageAddr = page;
        CountBase = page;
        SiblingBase = page + SiblingPageOffset;
        _installed = true;   // last: publishes every field above (release/acquire)
        return null;
    }

    /// <summary>Puts every field back to its vanilla value; never touches the page (D5: no
    /// copy-back, the page is leaked by design). Idempotent: a call while not installed is a
    /// no-op success, matching <see cref="ShopFlagsMirror.Restore"/>.</summary>
    public bool Restore(ICodePatcher patcher)
    {
        if (!Installed) return true;
        bool ok = true;
        foreach (var site in Sites)
            ok &= patcher.TryWrite(site.Addr, Encode(site.Vanilla));
        if (ok)
        {
            CountBase = Offsets.BagCountArray;
            SiblingBase = Offsets.SiblingListArray;
            _installed = false;
        }
        return ok;
    }

    /// <summary>D8's hidden-writer tripwire. Compares each old block against the snapshot taken
    /// the moment it was copied onto the new page; the first time either one has changed since,
    /// this returns the one-line warning naming the block -- and never again, even if the
    /// divergence continues or a second block also drifts, because one warning is already enough
    /// to tell the owner something still writes where nothing should. Null every other time
    /// (not installed, nothing changed, or already warned once). Round 2b: the compare window is
    /// <see cref="ListProperBytes"/>, not the full <see cref="BlockBytes"/> snapshot -- bytes
    /// +0x105..+0x10F of each old block hold a live game state dword at +0x108 (read live
    /// 2026-08-31), not list data, and comparing them would false-fire on that dword's own
    /// ordinary churn.</summary>
    public string? CheckOldBlocks(ICodePatcher patcher)
    {
        if (!Installed || _warnedOnce || _oldCountSnapshot == null || _oldSiblingSnapshot == null) return null;
        if (patcher.TryRead(Offsets.BagCountArray, ListProperBytes, out var count) && !SamePrefix(count, _oldCountSnapshot))
            return Warned($"something still writes the game's old item count block at 0x{Offsets.BagCountArray:X}; a code reference the relocation missed.");
        if (patcher.TryRead(Offsets.SiblingListArray, ListProperBytes, out var sibling) && !SamePrefix(sibling, _oldSiblingSnapshot))
            return Warned($"something still writes the game's old item flag block at 0x{Offsets.SiblingListArray:X}; a code reference the relocation missed.");
        return null;
    }

    private string Warned(string message) { _warnedOnce = true; return message; }

    private bool SnapshotOldBlocks(ICodePatcher patcher, out byte[] countBlock, out byte[] siblingBlock)
    {
        siblingBlock = Array.Empty<byte>();   // assigned unconditionally: an out param must be set on every path
        if (!patcher.TryRead(Offsets.BagCountArray, BlockBytes, out countBlock)) return false;
        if (!patcher.TryRead(Offsets.SiblingListArray, BlockBytes, out siblingBlock)) return false;
        _oldCountSnapshot = countBlock;
        _oldSiblingSnapshot = siblingBlock;
        return true;
    }

    /// <summary>True when <paramref name="fresh"/> (a <see cref="ListProperBytes"/>-long read)
    /// matches the first <c>fresh.Length</c> bytes of <paramref name="snapshot"/> (a full
    /// <see cref="BlockBytes"/>-long capture) -- lets <see cref="CheckOldBlocks"/> compare the
    /// list proper against a snapshot that is deliberately longer than the window it watches.</summary>
    private static bool SamePrefix(byte[] fresh, byte[] snapshot)
    {
        for (int i = 0; i < fresh.Length; i++) if (fresh[i] != snapshot[i]) return false;
        return true;
    }
}
