using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Moves the game's item count lists somewhere roomier, so more than eleven new items fit.
/// The game keeps one count byte per item in a block that ends where Ramza's roster row starts, with a
/// second per item byte list beside it, so a new id past that edge would read and write Ramza's save data.
/// This copies both lists onto a page of ours and points the 55 code fields that name the old blocks at it
/// (the table is in ListRelocation.Sites.cs). As in <see cref="ShopFlagsMirror"/>, every field must still
/// hold its vanilla value before anything is written, and a refusal puts the changed fields back in reverse.
/// The lists move for good, so nothing syncs them later, and <see cref="CheckOldBlocks"/> warns once if an
/// old block changes. Restore only puts the 55 fields back, since <see cref="INearAllocator"/> never frees.
/// </summary>
internal sealed partial class ListRelocation
{
    // Install runs at boot before any hook thread exists, but once armed
    // ExtendedInventory.BagCountBase reads the result from any thread, so the flag is volatile
    // and set last.
    private volatile bool _installed;

    /// <summary>True once every field points at the new page.</summary>
    public bool Installed => _installed;

    /// <summary>The page the two lists live on, or 0 before Install.</summary>
    public long PageAddr { get; private set; }

    /// <summary>Where the bag counts are right now, our page once installed and the game's own
    /// <see cref="Offsets.BagCountArray"/> before that.</summary>
    public long CountBase { get; private set; } = Offsets.BagCountArray;

    /// <summary>Where the second per item list is right now, our page once installed and the game's own
    /// <see cref="Offsets.SiblingListArray"/> before that.</summary>
    public long SiblingBase { get; private set; } = Offsets.SiblingListArray;

    private byte[]? _oldCountSnapshot, _oldSiblingSnapshot;
    private bool _warnedOnce;

    /// <summary>Null on success, else why not. Safe to call twice. Every field is checked and the page is filled
    /// before the first field is rewritten, so a refusal before that point has touched nothing the game uses.</summary>
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
        _installed = true;   // last, so a reader on another thread sees the addresses above first
        return null;
    }

    /// <summary>Points every field back at the game's own lists. Calling it before Install does nothing
    /// and succeeds, as in <see cref="ShopFlagsMirror.Restore"/>.</summary>
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

    /// <summary>Catches a writer the move missed. Each old block is compared with what it held when it was copied,
    /// and the first change gets one warning naming the block, then never again.</summary>
    /// <remarks>Only <see cref="ListProperBytes"/> is compared, because +0x105 to +0x10F of <see cref="BlockBytes"/>
    /// holds a game dword at +0x108 that changes on its own, seen live.</remarks>
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
        siblingBlock = Array.Empty<byte>();
        if (!patcher.TryRead(Offsets.BagCountArray, BlockBytes, out countBlock)) return false;
        if (!patcher.TryRead(Offsets.SiblingListArray, BlockBytes, out siblingBlock)) return false;
        _oldCountSnapshot = countBlock;
        _oldSiblingSnapshot = siblingBlock;
        return true;
    }

    /// <summary>The snapshot is the whole block and the fresh read only its list part.</summary>
    private static bool SamePrefix(byte[] fresh, byte[] snapshot)
    {
        for (int i = 0; i < fresh.Length; i++) if (fresh[i] != snapshot[i]) return false;
        return true;
    }
}
