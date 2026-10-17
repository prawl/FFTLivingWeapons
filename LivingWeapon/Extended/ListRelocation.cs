using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Lifts the extended inventory past eleven items. The game keeps owned counts as one
/// byte per item in a block that ends where Ramza's roster row begins, plus a second per-item byte list
/// beside it, so a new id past that edge reads and writes Ramza's save data. This copies both lists onto
/// a page the mod owns and re-points the 55 code fields that name the old blocks (table in
/// ListRelocation.Sites.cs). As in <see cref="ShopFlagsMirror"/>, every field must read vanilla before any
/// write and a refusal restores the changed fields in reverse. It is a move, so there is no resync;
/// <see cref="CheckOldBlocks"/> warns once if an old block changes later. Restore puts only the 55 fields
/// back and the page is leaked, as <see cref="INearAllocator"/> never frees.
/// </summary>
internal sealed partial class ListRelocation
{
    // Install runs on the boot thread before the game's hook threads exist, but
    // ExtendedInventory.BagCountBase reads this from any thread once armed, so the flag is volatile
    // and written last.
    private volatile bool _installed;

    /// <summary>True once every field points at the new page.</summary>
    public bool Installed => _installed;

    /// <summary>The page the two lists live on once installed; 0 before that.</summary>
    public long PageAddr { get; private set; }

    /// <summary>Where the bag counts resolve right now. The new page's count copy once
    /// installed, else <see cref="Offsets.BagCountArray"/> (the vanilla block).</summary>
    public long CountBase { get; private set; } = Offsets.BagCountArray;

    /// <summary>Where the sibling flag list resolves right now. The new page's copy once
    /// installed, else <see cref="Offsets.SiblingListArray"/> (the vanilla block).</summary>
    public long SiblingBase { get; private set; } = Offsets.SiblingListArray;

    private byte[]? _oldCountSnapshot, _oldSiblingSnapshot;
    private bool _warnedOnce;

    /// <summary>Null on success, else the refusal (nothing changed). Idempotent once installed.
    /// Order is check all 55 fields, allocate and zero the page, copy both lists, then
    /// rewrite the first field, so an earlier refusal leaves the process untouched.</summary>
    public string? Install(ICodePatcher patcher, INearAllocator allocator)
    {
        if (Installed) return null;
        foreach (var site in Sites)
        {
            if (!patcher.TryRead(Live(site), 4, out var bytes))
                return $"list-relocation: 0x{Live(site):X} is unreadable (expected vanilla 0x{site.Vanilla:X8})";
            int now = BitConverter.ToInt32(bytes, 0);
            if (now != site.Vanilla)
                return $"list-relocation: 0x{Live(site):X} reads 0x{now:X8}, expected vanilla 0x{site.Vanilla:X8} (already redirected or moved)";
        }

        long page = allocator.Alloc(PageSize, ImageBase.Base);
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
            if (!patcher.TryWrite(Live(site), Encode(NewField(site, page))))
            {
                for (int i = written.Count - 1; i >= 0; i--)
                    patcher.TryWrite(Live(written[i]), Encode(written[i].Vanilla));
                return $"list-relocation: field write refused at 0x{Live(site):X}";
            }
            written.Add(site);
        }

        PageAddr = page;
        CountBase = page;
        SiblingBase = page + SiblingPageOffset;
        _installed = true;   // last, publishes the fields above
        return null;
    }

    /// <summary>Puts every field back to its vanilla value and leaves the page alone.
    /// A call while not installed is a no-op success, as in <see cref="ShopFlagsMirror.Restore"/>.</summary>
    public bool Restore(ICodePatcher patcher)
    {
        if (!Installed) return true;
        bool ok = true;
        foreach (var site in Sites)
            ok &= patcher.TryWrite(Live(site), Encode(site.Vanilla));
        if (ok)
        {
            CountBase = Offsets.BagCountArray;
            SiblingBase = Offsets.SiblingListArray;
            _installed = false;
        }
        return ok;
    }

    /// <summary>hidden-writer tripwire. Compares each old block with its snapshot from the copy;
    /// the first change returns a one-line warning naming the block, and every later call returns
    /// null. Only <see cref="ListProperBytes"/> is compared, not all of <see cref="BlockBytes"/>
    /// because +0x105..+0x10F holds a live game state dword at +0x108 (read live
    /// 2026-08-31) that changes on its own.</summary>
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
        siblingBlock = Array.Empty<byte>();   // out param must be set on every path
        if (!patcher.TryRead(Offsets.BagCountArray, BlockBytes, out countBlock)) return false;
        if (!patcher.TryRead(Offsets.SiblingListArray, BlockBytes, out siblingBlock)) return false;
        _oldCountSnapshot = countBlock;
        _oldSiblingSnapshot = siblingBlock;
        return true;
    }

    /// <summary>True when <paramref name="fresh"/> matches the first <c>fresh.Length</c> bytes of
    /// the longer <paramref name="snapshot"/>.</summary>
    private static bool SamePrefix(byte[] fresh, byte[] snapshot)
    {
        for (int i = 0; i < fresh.Length; i++) if (fresh[i] != snapshot[i]) return false;
        return true;
    }
}
