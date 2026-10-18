using System;
using System.IO;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-381: <see cref="ModdedPac"/> against real temp files under <see cref="Path.GetTempPath"/>
/// (never the real game's modded.pac -- the game is live while this suite runs). Every test
/// cleans up its own file. Covers the cached-offset fast path, the full-search fallback, and
/// every failure mode degrading to a result instead of a throw.
/// </summary>
public class ModdedPacTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"moddedpac_test_{Guid.NewGuid():N}.bin");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    private static byte[] Filler(int len, byte value = 0xAA)
    {
        var b = new byte[len];
        for (int i = 0; i < len; i++) b[i] = value;
        return b;
    }

    private void WriteFile(byte[] bytes) => File.WriteAllBytes(_path, bytes);
    private byte[] ReadFile() => File.ReadAllBytes(_path);

    [Fact]
    public void Patch_HappyPath_PatchesInPlace_ByteExact()
    {
        byte[] oldBytes = { 0x01, 0x02, 0x03, 0x04 };
        byte[] newBytes = { 0x11, 0x12, 0x13, 0x14 };
        var content = Filler(100);
        Array.Copy(oldBytes, 0, content, 40, oldBytes.Length);
        WriteFile(content);

        var pac = new ModdedPac(_path);
        var result = pac.Patch("key1", oldBytes, newBytes);

        Assert.Equal(PacPatchResult.Patched, result);
        var expected = (byte[])content.Clone();
        Array.Copy(newBytes, 0, expected, 40, newBytes.Length);
        Assert.Equal(expected, ReadFile());
    }

    [Fact]
    public void PatchBatch_WriteOpenRefused_RecordsFailureDetail()
    {
        byte[] oldBytes = { 0x01, 0x02, 0x03, 0x04 };
        byte[] newBytes = { 0x11, 0x12, 0x13, 0x14 };
        var content = Filler(100);
        Array.Copy(oldBytes, 0, content, 40, oldBytes.Length);
        WriteFile(content);
        var pac = new ModdedPac(_path);

        PacPatchResult refused;
        // A read handle that shares only reads lets the search open through but refuses the write open.
        using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
            refused = pac.Patch("key1", oldBytes, newBytes);

        Assert.Equal(PacPatchResult.Failed, refused);
        Assert.Equal("IOException", pac.LastFailure("key1")?.ExceptionType);
        Assert.StartsWith("at write open, ", pac.LastFailure("key1")?.Message);

        Assert.Equal(PacPatchResult.Patched, pac.Patch("key1", oldBytes, newBytes));
        Assert.Null(pac.LastFailure("key1"));
    }

    [Fact]
    public void PatchBatch_RetryAfterLandedWrite_ReportsAlreadyCurrent()
    {
        byte[] oldBytes = { 0x01, 0x02, 0x03, 0x04 };
        byte[] newBytes = { 0x11, 0x12, 0x13, 0x14 };
        var content = Filler(100);
        Array.Copy(newBytes, 0, content, 40, newBytes.Length);   // the write landed but was never verified
        WriteFile(content);
        var pac = new ModdedPac(_path);

        var first = pac.PatchBatch(new (string, byte[], byte[], byte[]?)[] { ("key1", oldBytes, newBytes, null) });
        Assert.Equal(PacPatchResult.AlreadyCurrent, first["key1"]);

        // A second copy of the new bytes makes any fresh search Ambiguous, so AlreadyCurrent proves the cached offset answered.
        var twice = new byte[content.Length + newBytes.Length];
        Array.Copy(content, twice, content.Length);
        Array.Copy(newBytes, 0, twice, content.Length, newBytes.Length);
        File.WriteAllBytes(_path, twice);

        var second = pac.PatchBatch(new (string, byte[], byte[], byte[]?)[] { ("key1", oldBytes, newBytes, null) });
        Assert.Equal(PacPatchResult.AlreadyCurrent, second["key1"]);
    }

    [Fact]
    public void PatchBatch_PriorTargetNeedle_WritesWhenPacHoldsIt()
    {
        byte[] baseBytes = { 0x01, 0x02, 0x03, 0x04 };
        byte[] tier1 = { 0x11, 0x12, 0x13, 0x14 };
        byte[] tier2 = { 0x21, 0x22, 0x23, 0x24 };
        var content = Filler(100);
        Array.Copy(tier1, 0, content, 40, tier1.Length);
        WriteFile(content);
        var pac = new ModdedPac(_path);

        var results = pac.PatchBatch(new (string, byte[], byte[], byte[]?)[] { ("key1", baseBytes, tier2, tier1) });

        Assert.Equal(PacPatchResult.Patched, results["key1"]);
        Assert.Equal(tier2, new ArraySegment<byte>(ReadFile(), 40, 4).ToArray());
    }

    [Fact]
    public void Patch_LengthMismatch_ReturnsFailed_LeavesFileUnchanged()
    {
        byte[] oldBytes = { 0x01, 0x02, 0x03, 0x04 };
        byte[] newBytes = { 0x11, 0x12, 0x13 };   // shorter -- can never patch in place
        var content = Filler(50);
        Array.Copy(oldBytes, 0, content, 10, oldBytes.Length);
        WriteFile(content);

        var pac = new ModdedPac(_path);
        var result = pac.Patch("key1", oldBytes, newBytes);

        Assert.Equal(PacPatchResult.Failed, result);
        Assert.Equal(content, ReadFile());
    }

    [Fact]
    public void Patch_TwoOccurrences_ReturnsAmbiguous_LeavesFileUnchanged()
    {
        byte[] oldBytes = { 0x01, 0x02, 0x03, 0x04 };
        byte[] newBytes = { 0x11, 0x12, 0x13, 0x14 };
        var content = Filler(100);
        Array.Copy(oldBytes, 0, content, 10, oldBytes.Length);
        Array.Copy(oldBytes, 0, content, 60, oldBytes.Length);
        WriteFile(content);

        var pac = new ModdedPac(_path);
        var result = pac.Patch("key1", oldBytes, newBytes);

        Assert.Equal(PacPatchResult.Ambiguous, result);
        Assert.Equal(content, ReadFile());
    }

    [Fact]
    public void Patch_NeedleAbsent_ReturnsNotFound_LeavesFileUnchanged()
    {
        byte[] oldBytes = { 0x01, 0x02, 0x03, 0x04 };
        byte[] newBytes = { 0x11, 0x12, 0x13, 0x14 };
        var content = Filler(50);   // oldBytes never appears
        WriteFile(content);

        var pac = new ModdedPac(_path);
        var result = pac.Patch("key1", oldBytes, newBytes);

        Assert.Equal(PacPatchResult.NotFound, result);
        Assert.Equal(content, ReadFile());
    }

    [Fact]
    public void Patch_MissingFile_ReturnsUnavailable_DoesNotThrow()
    {
        // deliberately never call WriteFile -- _path does not exist
        var pac = new ModdedPac(_path);
        var ex = Record.Exception(() =>
        {
            var result = pac.Patch("key1", new byte[] { 1, 2 }, new byte[] { 3, 4 });
            Assert.Equal(PacPatchResult.Unavailable, result);
        });

        Assert.Null(ex);
    }

    [Fact]
    public void Patch_SecondCallOnSameKey_ReusesCachedOffset_RatherThanRescanning()
    {
        byte[] oldBytes = { 0x01, 0x02, 0x03, 0x04 };
        byte[] newBytes = { 0x11, 0x12, 0x13, 0x14 };
        var content = Filler(100);
        Array.Copy(oldBytes, 0, content, 20, oldBytes.Length);
        WriteFile(content);

        var pac = new ModdedPac(_path);
        var first = pac.Patch("key1", oldBytes, newBytes);
        Assert.Equal(PacPatchResult.Patched, first);

        // Append a SECOND copy of newBytes to the file. A fresh full scan for newBytes would now
        // find it twice (Ambiguous). If the second Patch call below still returns Patched, that
        // proves it used the cached offset from the first call rather than re-scanning.
        var withSecondCopy = ReadFile();
        var extended = new byte[withSecondCopy.Length + newBytes.Length];
        Array.Copy(withSecondCopy, extended, withSecondCopy.Length);
        Array.Copy(newBytes, 0, extended, withSecondCopy.Length, newBytes.Length);
        File.WriteAllBytes(_path, extended);

        byte[] secondNew = { 0x21, 0x22, 0x23, 0x24 };
        var second = pac.Patch("key1", newBytes, secondNew);

        Assert.Equal(PacPatchResult.Patched, second);
        var afterSecond = ReadFile();
        // only the cached-offset copy (at 20) was rewritten; the appended trailing copy of
        // newBytes at the tail is untouched, which is the proof the cache -- not a fresh full
        // scan (which would have found two occurrences of newBytes and returned Ambiguous) --
        // drove this write.
        Assert.Equal(secondNew, new ArraySegment<byte>(afterSecond, 20, 4).ToArray());
        Assert.Equal(newBytes, new ArraySegment<byte>(afterSecond, afterSecond.Length - 4, 4).ToArray());
    }

    // ---- R2-6: AlreadyCurrent must be a real, tested outcome -- the cached path is the only way
    // to reach it (a fresh full search always looks for oldBytes specifically, so it can only
    // land on AlreadyCurrent once a prior Patch has already written newBytes and a later call
    // asks for the very same old/new pair again). ----

    [Fact]
    public void Patch_AlreadyCurrent_WhenCachedTargetAlreadyInPlace_WritesNothing()
    {
        byte[] oldBytes = { 0x01, 0x02, 0x03, 0x04 };
        byte[] newBytes = { 0x11, 0x12, 0x13, 0x14 };
        var content = Filler(100);
        Array.Copy(oldBytes, 0, content, 20, oldBytes.Length);
        WriteFile(content);

        var pac = new ModdedPac(_path);
        var first = pac.Patch("key1", oldBytes, newBytes);
        Assert.Equal(PacPatchResult.Patched, first);
        var afterFirst = ReadFile();

        // Same call again: the cache still points at offset 20, which now already holds
        // newBytes -- nothing left to write.
        var second = pac.Patch("key1", oldBytes, newBytes);

        Assert.Equal(PacPatchResult.AlreadyCurrent, second);
        Assert.Equal(afterFirst, ReadFile());
    }

    // ---- R2-2/R2-9: PatchBatch resolves cache-known offsets WITHOUT a pass, and shares one pass
    // for whatever remains unresolved. Proven the same way Patch_SecondCallOnSameKey_... proves
    // the single-key cache above: append a second copy of the cached key's CURRENT bytes so a
    // fresh full search for them would be Ambiguous, then show the batch call still succeeds --
    // that is only possible if the cached offset was used directly rather than swept into the
    // shared pass. ----

    [Fact]
    public void PatchBatch_MixOfCachedAndUnresolvedTriples_PatchesAll_CachedOneSkipsThePass()
    {
        byte[] oldX = { 0x01, 0x02, 0x03, 0x04 };
        byte[] newX = { 0x11, 0x12, 0x13, 0x14 };
        byte[] oldY = { 0x21, 0x22, 0x23, 0x24 };
        byte[] newY = { 0x31, 0x32, 0x33, 0x34 };
        var content = Filler(200);
        Array.Copy(oldX, 0, content, 20, oldX.Length);
        Array.Copy(oldY, 0, content, 100, oldY.Length);
        WriteFile(content);

        var pac = new ModdedPac(_path);
        var seed = pac.Patch("keyX", oldX, newX);   // establishes the cache: keyX -> offset 20
        Assert.Equal(PacPatchResult.Patched, seed);

        // Append a SECOND copy of newX. A fresh full search for newX would now find it twice
        // (Ambiguous) -- if the batch call below still returns Patched for keyX, that proves it
        // used the cached offset rather than folding keyX into the shared pass.
        var afterSeed = ReadFile();
        var extended = new byte[afterSeed.Length + newX.Length];
        Array.Copy(afterSeed, extended, afterSeed.Length);
        Array.Copy(newX, 0, extended, afterSeed.Length, newX.Length);
        File.WriteAllBytes(_path, extended);

        byte[] secondX = { 0x41, 0x42, 0x43, 0x44 };
        var results = pac.PatchBatch(new (string, byte[], byte[], byte[]?)[]
        {
            ("keyX", newX, secondX, null),   // cache-resolved
            ("keyY", oldY, newY, null),      // unresolved -- needs the shared pass
        });

        Assert.Equal(PacPatchResult.Patched, results["keyX"]);
        Assert.Equal(PacPatchResult.Patched, results["keyY"]);
        var final = ReadFile();
        Assert.Equal(secondX, new ArraySegment<byte>(final, 20, 4).ToArray());
        Assert.Equal(newY, new ArraySegment<byte>(final, 100, 4).ToArray());
        // the appended trailing copy of newX is untouched -- only the cached offset was rewritten
        Assert.Equal(newX, new ArraySegment<byte>(final, final.Length - 4, 4).ToArray());
    }

    [Fact]
    public void Patch_StaleCachedOffset_FallsBackToFullSearch_StillSucceeds()
    {
        byte[] oldBytes = { 0x01, 0x02, 0x03, 0x04 };
        byte[] newBytes = { 0x11, 0x12, 0x13, 0x14 };
        var content = Filler(100);
        Array.Copy(oldBytes, 0, content, 20, oldBytes.Length);
        WriteFile(content);

        var pac = new ModdedPac(_path);
        var first = pac.Patch("key1", oldBytes, newBytes);
        Assert.Equal(PacPatchResult.Patched, first);

        // Simulate a deploy re-merging the pac mid-session: the bytes at the cached offset (20)
        // now match neither the old nor the new value the next Patch call will look for. Move
        // the "current" content (newBytes at 20) somewhere else entirely and blank offset 20.
        var mutated = Filler(150);
        Array.Copy(newBytes, 0, mutated, 90, newBytes.Length);   // moved elsewhere
        File.WriteAllBytes(_path, mutated);

        byte[] thirdNew = { 0x31, 0x32, 0x33, 0x34 };
        var second = pac.Patch("key1", newBytes, thirdNew);

        Assert.Equal(PacPatchResult.Patched, second);
        var after = ReadFile();
        Assert.Equal(thirdNew, new ArraySegment<byte>(after, 90, 4).ToArray());
    }
}
