using System;
using System.IO;

namespace LivingWeapon;

/// <summary>
/// Split from <see cref="ModdedPac"/>. Reads, compares, writes and verifies the bytes at an
/// offset already believed correct. ModdedPac.cs decides which offset to trust and when to fall back to a
/// <see cref="PacScan"/> search. The two halves fail differently (stale cache vs. share violation).
/// </summary>
internal sealed partial class ModdedPac
{
    /// <summary>Cached-offset fast path. Read the bytes at <c>offset</c> and act on what is there.
    /// <paramref name="stale"/> is true only when they match neither old nor new, the cue for a full search.
    /// Any I/O failure (FileShare, truncated file mid-session) is final.</summary>
    private PacPatchResult TryPatchAtOffset(string cacheKey, long offset, byte[] oldBytes, byte[] newBytes, byte[]? priorTarget, out bool stale)
    {
        stale = false;
        try
        {
            using var stream = new FileStream(_pacPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (offset < 0 || offset + oldBytes.Length > stream.Length) { stale = true; return PacPatchResult.Failed; }
            stream.Position = offset;
            var atOffset = ReadExactly(stream, oldBytes.Length);
            if (atOffset == null) return Fail(cacheKey, "ShortRead", 0, "could not read the full entry at the cached offset");

            if (BytesEqual(atOffset, newBytes)) { ClearFailure(cacheKey); return PacPatchResult.AlreadyCurrent; }
            if (!BytesEqual(atOffset, oldBytes) && (priorTarget == null || !BytesEqual(atOffset, priorTarget))) { stale = true; return PacPatchResult.Failed; }
        }
        catch { return PacPatchResult.Unavailable; }

        return WriteAndVerify(cacheKey, offset, newBytes);
    }

    /// <summary>Write at the offset, flush to disk (<see cref="FileStream.Flush(bool)"/> with
    /// <c>flushToDisk. True</c>, the probe's flush + fsync), then reopen and compare. Never throws; any
    /// failure returns <see cref="PacPatchResult.Failed"/>.</summary>
    private PacPatchResult WriteAndVerify(string cacheKey, long offset, byte[] newBytes)
    {
        string stage = "write open";
        try
        {
            using (var writeStream = new FileStream(_pacPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                writeStream.Position = offset;
                stage = "write";
                writeStream.Write(newBytes, 0, newBytes.Length);
                stage = "flush";
                writeStream.Flush(flushToDisk: true);
            }

            stage = "verify open";
            using var verifyStream = new FileStream(_pacPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stage = "verify read";
            verifyStream.Position = offset;
            var readBack = ReadExactly(verifyStream, newBytes.Length);
            if (readBack == null)
                return Fail(cacheKey, "ShortRead", 0, "at verify read, could not read the full entry back after the write");
            if (!BytesEqual(readBack, newBytes))
                return Fail(cacheKey, "ReadbackMismatch", 0, "at verify read, the bytes read back after the write did not match");
            ClearFailure(cacheKey);
            return PacPatchResult.Patched;
        }
        catch (Exception ex) { return Fail(cacheKey, ex.GetType().Name, ex.HResult & 0xFFFF, $"at {stage}, {ex.Message}"); }
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
