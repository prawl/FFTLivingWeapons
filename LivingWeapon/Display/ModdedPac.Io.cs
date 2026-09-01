using System.IO;

namespace LivingWeapon;

/// <summary>
/// LW-381 (200-line split from ModdedPac.cs): the physical byte-level half of
/// <see cref="ModdedPac"/> -- given an OFFSET already believed correct (from the cache or from a
/// <see cref="PacScan"/> search), read/compare/write/verify the bytes actually sitting there. The
/// other half (ModdedPac.cs) decides WHICH offset to trust and WHEN to fall back to a search; this
/// file only ever acts once that decision is made. A real seam, not a line-count dodge: "where/
/// whether to patch" vs. "how to physically apply one already-located patch" are different
/// concerns with different failure shapes (a stale cache vs. a share violation).
/// </summary>
internal sealed partial class ModdedPac
{
    /// <summary>Cached-offset fast path: seek there, read exactly <c>oldBytes.Length</c> bytes,
    /// and act on what is actually sitting there -- never assume the cache is still correct.
    /// <paramref name="stale"/> is true only for the "matches neither" case, the signal to fall
    /// back to a full search; every other outcome (including any I/O failure -- FileShare or a
    /// truncated file mid-session are exactly the shapes a live game could produce) is final.</summary>
    private PacPatchResult TryPatchAtOffset(long offset, byte[] oldBytes, byte[] newBytes, out bool stale)
    {
        stale = false;
        try
        {
            using var stream = new FileStream(_pacPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (offset < 0 || offset + oldBytes.Length > stream.Length) { stale = true; return PacPatchResult.Failed; }
            stream.Position = offset;
            var atOffset = ReadExactly(stream, oldBytes.Length);
            if (atOffset == null) return PacPatchResult.Failed;

            if (BytesEqual(atOffset, newBytes)) return PacPatchResult.AlreadyCurrent;
            if (!BytesEqual(atOffset, oldBytes)) { stale = true; return PacPatchResult.Failed; }
        }
        catch { return PacPatchResult.Unavailable; }

        return WriteAndVerify(offset, newBytes);
    }

    /// <summary>Open for write, seek, write, flush to disk (the .NET equivalent of the probe's
    /// <c>flush</c> + <c>fsync</c>: <see cref="FileStream.Flush(bool)"/> with
    /// <c>flushToDisk: true</c>), then reopen for read and compare byte-exact. Never throws --
    /// any failure at any of these steps becomes <see cref="PacPatchResult.Failed"/>, since by
    /// this point the location is known good and the only remaining risk is the write itself
    /// (a share violation, disk pressure, or the file changing mid-operation).</summary>
    private PacPatchResult WriteAndVerify(long offset, byte[] newBytes)
    {
        try
        {
            using (var writeStream = new FileStream(_pacPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                writeStream.Position = offset;
                writeStream.Write(newBytes, 0, newBytes.Length);
                writeStream.Flush(flushToDisk: true);
            }

            using var verifyStream = new FileStream(_pacPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            verifyStream.Position = offset;
            var readBack = ReadExactly(verifyStream, newBytes.Length);
            return readBack != null && BytesEqual(readBack, newBytes) ? PacPatchResult.Patched : PacPatchResult.Failed;
        }
        catch { return PacPatchResult.Failed; }
    }

    private static byte[]? ReadExactly(Stream stream, int count)
    {
        var buffer = new byte[count];
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(buffer, total, count - total);
            if (read <= 0) return null;
            total += read;
        }
        return buffer;
    }

    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}
