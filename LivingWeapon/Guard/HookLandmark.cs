namespace LivingWeapon;

/// <summary>
/// Prologue-landmark verifier for code-hook targets. No project dependencies, so a sibling mod
/// can copy the file (same as FingerprintGuard.cs). Compares only the leading expected.Length
/// bytes of <paramref name="actual"/>; null or shorter is a mismatch.
/// A game patch can shift a function entry so the old address lands mid-function, and a detour
/// there corrupts it (the 1.5.1 FnSetTextString incident, docs/research/PORT_1.5.1_OFFSETS.md).
/// Verify turns that into a refusal instead of a crash.
/// </summary>
internal static class HookLandmark
{
    public static bool Verify(byte[]? actual, byte[] expected)
    {
        if (actual == null || actual.Length < expected.Length) return false;
        for (int i = 0; i < expected.Length; i++)
            if (actual[i] != expected[i]) return false;
        return true;
    }
}
