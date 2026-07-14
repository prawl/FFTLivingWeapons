using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// HookLandmark.cs: the portable prologue-verifier core (dependency-free BY CONTRACT, the
/// FingerprintGuard.cs copy-file pattern). Semantics under test: Verify is a PREFIX compare
/// (actual must be at least as long as expected, and only the leading expected.Length bytes are
/// compared; a longer actual is fine, whatever follows the prefix is ignored).
/// </summary>
public class HookLandmarkTests
{
    private static readonly byte[] Expected = { 0x48, 0x83, 0xEC, 0x28, 0x48, 0x8B, 0x41, 0x10, 0x44, 0x8A, 0xC2 };

    [Fact]
    public void Exact_match_is_true()
    {
        byte[] actual = { 0x48, 0x83, 0xEC, 0x28, 0x48, 0x8B, 0x41, 0x10, 0x44, 0x8A, 0xC2 };

        Assert.True(HookLandmark.Verify(actual, Expected));
    }

    [Fact]
    public void Single_byte_mismatch_is_false()
    {
        byte[] actual = { 0x48, 0x83, 0xEC, 0x28, 0x48, 0x8B, 0x41, 0x10, 0x44, 0x8A, 0xC3 };

        Assert.False(HookLandmark.Verify(actual, Expected));
    }

    [Fact]
    public void Actual_shorter_than_expected_is_false()
    {
        byte[] actual = { 0x48, 0x83, 0xEC, 0x28 };

        Assert.False(HookLandmark.Verify(actual, Expected));
    }

    [Fact]
    public void Actual_null_is_false()
    {
        Assert.False(HookLandmark.Verify(null, Expected));
    }

    [Fact]
    public void Actual_longer_than_expected_compares_only_the_prefix()
    {
        // Prefix-compare semantics (pinned by this test): trailing bytes past expected.Length are
        // never examined, matched or not.
        byte[] actual = { 0x48, 0x83, 0xEC, 0x28, 0x48, 0x8B, 0x41, 0x10, 0x44, 0x8A, 0xC2, 0xFF, 0x00, 0x11 };

        Assert.True(HookLandmark.Verify(actual, Expected));
    }
}
