using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// PromptSwap's diagnostic sampling half, split out of PromptSwap.cs for the 200-line
/// trigger. Both samplers only observe, from TryPrepareSwap on the game's SetTextString thread,
/// so the HashSet state needs no lock. The head sampler logs the first dozen unique decodable
/// prompt heads. The struct sampler (step 1b) runs on every commit, so it also sees the
/// unreadable or undecodable rdx values that the 1.5.1 capture showed are most of them.
/// </summary>
internal sealed partial class PromptSwap
{
    // Cap on head sample lines per session, so a text-heavy session cannot flood the log (the
    // LW-69 lesson). The facing prompt appears within the first few commits of a turn.
    private const int SampleCap = 12;

    // Struct sampler cap (LW-89 step 1b), independent of SampleCap. Six covers the shapes seen
    // (facing prompt, tile prompt, unit name, ...).
    private const int StructSampleCap = 6;

    // Bytes dumped for the struct sample. The hypothesized capacity-check field at +0x18 plus
    // headroom, and a look-behind in case textPtr sits off the string-object base.
    private const int StructRawLen = 32;
    private const int StructPreLen = 8;
    private const int DerefLen = 16;

    // Candidates below this are plausible user-mode pointers (under the 48-bit user/kernel split).
    private const long PlausiblePointerMax = 0x7FFFFFFFFFFF;

    // Only touched from TryPrepareSwap, which runs on the SetTextString thread, so no lock.
    private readonly HashSet<string> _sampledHeads = new();
    private readonly HashSet<string> _sampledStructs = new();

    /// <summary>Logs one decoded prompt head per unique text, up to SampleCap lines a session.</summary>
    private void SampleHead(string text)
    {
        if (_sampledHeads.Count < SampleCap && text.Length > 0 && _sampledHeads.Add(text))
            ModLogger.Debug(LogVerb.Toast, $"prompt head sample {_sampledHeads.Count} of {SampleCap}: \"{text}\"");
    }

    /// <summary>LW-89 step 1b. Logs at Debug a bounded window around textPtr (look-behind, raw bytes,
    /// the three capacity-check offsets, one pointer-chase), one line per unique 16-byte pattern, up
    /// to StructSampleCap a session. An unreadable textPtr counts against the cap via the
    /// "unreadable" key, so a persistent failure cannot log forever.</summary>
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

    /// <summary>Renders each byte as printable ASCII (0x20 to 0x7E) or a dot.</summary>
    private static string RenderPrintable(byte[] bytes)
    {
        var chars = new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
            chars[i] = bytes[i] is >= 0x20 and <= 0x7E ? (char)bytes[i] : '.';
        return new string(chars);
    }
}
