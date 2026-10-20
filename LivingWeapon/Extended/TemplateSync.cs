using System;

namespace LivingWeapon;

/// <summary>
/// Carries the menu order charts between our page and the save file, which expects them where they used to be.
/// While <see cref="TemplateRelocation"/> is armed the game works on our copies, but the save keeps the old shape, the
/// first 140 (or 261) chart words in order, new ids included, then the marker. On a save, <see cref="Project"/> runs
/// before the game copies the old block into the file, and writes as much of our chart as the old block holds into it.
/// On a load, <see cref="Adopt"/> runs after the game restores the old block, copies it back onto our page, and puts the
/// marker and the 0xFFFF wall back so the game's routines can walk it safely.
/// </summary>
internal static class TemplateSync
{
    /// <summary>The words before the first <see cref="TemplateSeat.EndMarker"/>, as they are (a 0x4000 badge stays on), cut to
    /// <paramref name="capacityWords"/> minus one so the marker still fits after them. <see cref="OldBlockImage"/> does the zero padding.</summary>
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

    /// <summary>The projection as bytes, zero padded to the old block's size. The projection was already cut to fit,
    /// so it never runs past <paramref name="spanBytes"/>.</summary>
    public static byte[] OldBlockImage(ushort[] projection, int spanBytes)
    {
        var bytes = new byte[spanBytes];
        for (int i = 0; i < projection.Length; i++) WriteWord(bytes, i, projection[i]);
        return bytes;
    }

    /// <summary>Turns an old chart block from the save into a page chart the game can walk without running off the end.
    /// With a 0x00FF marker already in it, it is copied as is and the 0xFFFF wall goes after it.</summary>
    /// <remarks>With no marker but a zero word, the marker takes that zero's slot. With every word a real id, which happens
    /// when the chart overflowed, the marker goes one word past the block and the wall after that.</remarks>
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

    /// <summary>Just before a save, writes each of our charts into the old block the save will copy, in one write that
    /// stays inside it. Called inside <see cref="SaveEdgeHooks.SerializeCore"/>'s try, before the game's save runs.</summary>
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

    /// <summary>Just after a load, copies each old block the game restored onto our page. Called from <see cref="SaveEdgeHooks.AfterApply"/>
    /// once the header reads, and before the counts are replayed and the charts seated, so the seat lands on the freshly loaded chart.</summary>
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
