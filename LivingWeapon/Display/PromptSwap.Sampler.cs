using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// PromptSwap's diagnostic sampling half (LW-89), split out once it pushed PromptSwap.cs past
/// the 200-line trigger: a real seam, not an evasion, since "decide whether/what to swap" and
/// "record what the live process actually looks like so a human can read it back later" are two
/// different responsibilities that happen to share TryPrepareSwap's one entry point. Both
/// samplers are pure observers, called from TryPrepareSwap on the game's own SetTextString
/// thread (see PromptSwap's class doc), so the HashSet state here needs no lock either.
///
/// The HEAD sampler (shipped first) logs the first dozen unique decodable prompt heads a
/// session ever sees, keyed on the decoded text. The STRUCT sampler (step 1b) is strictly wider:
/// it runs on every commit regardless of whether the head read or the ASCII decode succeeded, so
/// it is the one that can actually see an unreadable or undecodable rdx, which the live 1.5.1
/// capture showed is most of them.
/// </summary>
internal sealed partial class PromptSwap
{
    // Sampler bound: at most this many sample lines can ever reach the log in one session, so a
    // text-heavy session cannot flood the file (the LW-69 lesson). A dozen is plenty: the facing
    // prompt lands within the first few prompt commits of any battle turn.
    private const int SampleCap = 12;

    // Struct sampler bound (LW-89 step 1b): independent cap from SampleCap above. Six is enough
    // to see every shape a session's prompt traffic takes (facing prompt, tile prompt, unit name,
    // ...) without risking the same log flood the head sampler was already built to avoid.
    private const int StructSampleCap = 6;

    // Bytes dumped for the struct sample: enough to cover the hypothesized capacity-check field
    // at +0x18 with headroom, plus a look-behind in case textPtr itself sits a few bytes off the
    // true string-object base.
    private const int StructRawLen = 32;
    private const int StructPreLen = 8;
    private const int DerefLen = 16;

    // A candidate below this is treated as a plausible user-mode pointer (well under the
    // canonical 48-bit user/kernel split); zero or anything at/above it is not worth chasing.
    private const long PlausiblePointerMax = 0x7FFFFFFFFFFF;

    // Sampler state: only ever touched from TryPrepareSwap, which itself only ever runs on the
    // game's own SetTextString thread (see the class doc), so no lock is needed here either.
    private readonly HashSet<string> _sampledHeads = new();
    private readonly HashSet<string> _sampledStructs = new();

    /// <summary>Logs one decoded prompt head per unique text, up to SampleCap lines a session.</summary>
    private void SampleHead(string text)
    {
        if (_sampledHeads.Count < SampleCap && text.Length > 0 && _sampledHeads.Add(text))
            ModLogger.Debug(LogVerb.Toast, $"prompt head sample {_sampledHeads.Count} of {SampleCap}: \"{text}\"");
    }

    /// <summary>LW-89 step 1b: dumps a bounded window around textPtr (a look-behind, the raw
    /// bytes, and the three offsets the capacity-check hypothesis cares about, plus a one-level
    /// pointer-chase off the first of those) to the log at Debug tier, one line per unique
    /// 16-byte pattern seen at textPtr, up to StructSampleCap lines a session. A read failure at
    /// textPtr does not exempt it from the bound (see the "unreadable" key below): a session that
    /// never gets a readable rdx would otherwise burn every remaining call re-logging the exact
    /// same failure forever.</summary>
    private void SampleStruct(long textPtr)
    {
        if (_sampledStructs.Count >= StructSampleCap) return;

        bool rawOk = _mem.TryReadBytes(textPtr, StructRawLen, out var raw);
        string key = rawOk ? Convert.ToHexString(raw, 0, 16) : $"unreadable@0x{textPtr:X}";
        if (!_sampledStructs.Add(key)) return;

        string rawHex = rawOk ? Convert.ToHexString(raw) : "unreadable";
        string preHex = _mem.TryReadBytes(textPtr - StructPreLen, StructPreLen, out var pre)
            ? Convert.ToHexString(pre) : "unreadable";

        string q0Str, q10Str, q18Str, derefStr;
        if (rawOk)
        {
            ulong q0 = BitConverter.ToUInt64(raw, 0x00);
            ulong q10 = BitConverter.ToUInt64(raw, 0x10);
            ulong q18 = BitConverter.ToUInt64(raw, 0x18);
            q0Str = $"0x{q0:X}";
            q10Str = $"0x{q10:X}";
            q18Str = $"0x{q18:X}";

            derefStr = q0 != 0 && q0 <= PlausiblePointerMax && _mem.TryReadBytes((long)q0, DerefLen, out var deref)
                ? $"{Convert.ToHexString(deref)} \"{RenderPrintable(deref)}\""
                : "skipped";
        }
        else
        {
            q0Str = q10Str = q18Str = "unreadable";
            derefStr = "skipped";
        }

        ModLogger.Debug(LogVerb.Toast,
            $"prompt struct sample {_sampledStructs.Count} of {StructSampleCap}: rdx=0x{textPtr:X} " +
            $"pre={preHex} raw={rawHex} q0={q0Str} q10={q10Str} q18={q18Str} deref={derefStr}");
    }

    /// <summary>Renders each byte as its printable ASCII char (0x20 to 0x7E) or a dot, the same
    /// dot-for-non-printable convention a hex editor's side pane uses.</summary>
    private static string RenderPrintable(byte[] bytes)
    {
        var chars = new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
            chars[i] = bytes[i] is >= 0x20 and <= 0x7E ? (char)bytes[i] : '.';
        return new string(chars);
    }
}
