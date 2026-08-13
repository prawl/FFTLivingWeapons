namespace LivingWeapon;

/// <summary>
/// Portable prologue-landmark verifier for code-hook targets. COPY-FILE PORTABILITY CONTRACT
/// (the FingerprintGuard.cs pattern): zero project dependencies (no Mem, no ModLogger, no
/// IGameMemory, no Reloaded types), so a sibling mod adopts this mechanism by copying this one
/// file. Pure bytes in, bool out.
///
/// SEMANTICS: PREFIX compare. <paramref name="actual"/> must be at least as long as
/// <paramref name="expected"/>; only the leading expected.Length bytes are compared, so a longer
/// actual read (e.g. one caller's fixed-size buffer) is fine and whatever follows the prefix is
/// never examined. A null or too-short actual is always a mismatch.
///
/// WHY THIS EXISTS: a code-hook target address is never trusted just because it "reads like
/// code": a game patch can shift a function's entry so the OLD address becomes a mid-function
/// branch target, and installing a detour there corrupts the function (the 1.5.1 FnSetTextString
/// incident, docs/research/PORT_1.5.1_OFFSETS.md). Verify turns that into a refusal, not a crash.
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
