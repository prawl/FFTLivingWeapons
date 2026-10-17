using System;

namespace LivingWeapon;

/// <summary>
/// The pure policy for the template relocation, plus the two save and load edge operations.
/// The edge operations keep the save file's old chart blocks and the page's live charts in sync while
/// <see cref="TemplateRelocation"/> is armed. The relocation moves the charts the game works on, but the save field keeps
/// its old shape (F5), which is the first 140 (or 261) chart words in order, extended ids included, then the marker. On
/// save, <see cref="Project"/> runs before the game's serializer copies the old block. It reads the page, truncates to what
/// the old block holds, and writes that in. On load, <see cref="Adopt"/> runs after the game restores the old block. It
/// copies the span back onto the page and re-applies the marker rule and the 0xFFFF wall so the game's housekeeper can walk
/// it safely. Every write goes through the injected <see cref="ICodePatcher"/>.
/// </summary>
internal static class TemplateSync
{
    /// <summary>Pure. The page's words up to (not including) the first <see cref="TemplateSeat.EndMarker"/>, in page order
    /// with ids verbatim (a 0x4000 badge bit rides along unmasked), truncated to <paramref name="capacityWords"/>, 1 ids,
    /// then the marker. No zero-fill; <see cref="OldBlockImage"/> pads.</summary>
    public static ushort[] Projection(ushort[] pageWords, int capacityWords)
    {
        int end = Array.IndexOf(pageWords, TemplateSeat.EndMarker);
        int count = end < 0 ? pageWords.Length : end;
        count = Math.Min(count, capacityWords - 1);
        var result = new ushort[count + 1];
        Array.Copy(pageWords, result, count);
        result[count] = TemplateSeat.EndMarker;
        return result;
    }

    /// <summary>Pure <paramref name="projection"/> as little-endian words, zero-filled to <paramref name="spanBytes"/>,
    /// the old block's size (T6b. The projection is already truncated, so it never exceeds the span).</summary>
    public static byte[] OldBlockImage(ushort[] projection, int spanBytes)
    {
        var bytes = new byte[spanBytes];
        for (int i = 0; i < projection.Length; i++) WriteWord(bytes, i, projection[i]);
        return bytes;
    }

    /// <summary>Pure. The marker rule plus the 0xFFFF wall, applied to <paramref name="span"/>
    /// (an old chart block, read verbatim) to produce a <paramref name="regionBytes"/>-long page image. Cases:
    /// (a) the span already holds a 0x00FF marker. Copied unchanged, wall after the span's words (the game's walkers stop at the marker);
    /// (b) no marker but a zero word. The marker goes into that slot, wall after the span;
    /// (c) every word a nonzero non-marker id (the overflow shape, finding 10): marker appended one word past the span, wall right after it.</summary>
    public static byte[] RegionImage(byte[] span, int regionBytes)
    {
        int spanWords = span.Length / 2;
        var words = new ushort[spanWords];
        for (int i = 0; i < spanWords; i++) words[i] = ReadWord(span, i);

        int wallStart = spanWords;
        bool appendMarker = false;
        if (Array.IndexOf(words, TemplateSeat.EndMarker) < 0)
        {
            int zero = Array.IndexOf(words, (ushort)0);
            if (zero >= 0) words[zero] = TemplateSeat.EndMarker;
            else { appendMarker = true; wallStart = spanWords + 1; }
        }

        var region = new byte[regionBytes];
        for (int i = 0; i < spanWords; i++) WriteWord(region, i, words[i]);
        if (appendMarker) WriteWord(region, spanWords, TemplateSeat.EndMarker);
        for (int i = wallStart; (i + 1) * 2 <= regionBytes; i++) WriteWord(region, i, 0xFFFF);
        return region;
    }

    /// <summary>The serialize edge, a no-op when not installed. Per chart. Read the page region, project it to the
    /// old block's capacity, and write it in one write that stays inside the span.
    /// Called inside <see cref="SaveEdgeHooks.SerializeCore"/>'s try, before the original runs.</summary>
    public static void Project(ICodePatcher patcher, TemplateRelocation relocation)
    {
        if (!relocation.Installed) return;
        foreach (var chart in TemplateRelocation.Charts)
        {
            if (!patcher.TryRead(relocation.PageAddr + chart.PageOffset, chart.RegionBytes, out var regionBytes)) continue;
            var pageWords = new ushort[chart.RegionBytes / 2];
            for (int i = 0; i < pageWords.Length; i++) pageWords[i] = ReadWord(regionBytes, i);
            var projection = Projection(pageWords, chart.Capacity);
            patcher.TryWrite(chart.OldBase, OldBlockImage(projection, chart.SpanBytes));
        }
    }

    /// <summary>The load edge, a no-op when not installed. Per chart. Read the old block the game just restored,
    /// apply the marker rule and wall, and write it to the page in one write. Called from <see cref="SaveEdgeHooks.AfterApply"/>
    /// after <see cref="SaveEdgeHooks.ReadHeader"/> succeeds and before the bag/template replay, so the seat lands on a fresh page.</summary>
    public static void Adopt(ICodePatcher patcher, TemplateRelocation relocation)
    {
        if (!relocation.Installed) return;
        foreach (var chart in TemplateRelocation.Charts)
        {
            if (!patcher.TryRead(chart.OldBase, chart.SpanBytes, out var span)) continue;
            patcher.TryWrite(relocation.PageAddr + chart.PageOffset, RegionImage(span, chart.RegionBytes));
        }
    }

    private static ushort ReadWord(byte[] bytes, int wordIndex) => (ushort)(bytes[wordIndex * 2] | (bytes[wordIndex * 2 + 1] << 8));

    private static void WriteWord(byte[] bytes, int wordIndex, ushort value)
    {
        bytes[wordIndex * 2] = (byte)(value & 0xFF);
        bytes[wordIndex * 2 + 1] = (byte)(value >> 8);
    }
}
