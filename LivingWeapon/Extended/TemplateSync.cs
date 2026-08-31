using System;

namespace LivingWeapon;

/// <summary>
/// LW-371: the pure policy half of the template relocation, plus the two edge operations that keep
/// the save file's old chart blocks and the page's live charts in sync while
/// <see cref="TemplateRelocation"/> is armed.
///
/// WHY BOTH DIRECTIONS EXIST (D1/D4): the relocation moves the charts the GAME WORKS ON, but the
/// owner's ruling (F5) keeps the SAVE FIELD'S content unchanged in shape -- the first 140 (or 261)
/// chart words in order, extended ids included, then the marker. So on save, <see cref="Project"/>
/// runs BEFORE the game's own serializer copies the old block into the struct: it reads the page,
/// truncates to what the old block can hold, and writes that projection into the old block, so the
/// struct ends up with exactly what a chart of that size would have saved before this arc ever
/// existed (positions kept across loads, order preserved -- the owner's v1.1 ruling, D4). On load,
/// <see cref="Adopt"/> runs AFTER the game's own load-apply restores the old block from the struct:
/// it copies that restored span back onto the page and re-applies the marker rule (D3) and the
/// 0xFFFF wall (crash 1's fix, finding 8) so the page is always safe for the game's own housekeeper
/// to walk, insert into or delete from the instant the load returns.
///
/// Every write goes through the injected <see cref="ICodePatcher"/>, never a raw pointer deref.
/// </summary>
internal static class TemplateSync
{
    /// <summary>Pure: the page's words up to (not including) its first <see
    /// cref="TemplateSeat.EndMarker"/>, IN PAGE ORDER, any id kept verbatim (extended ids included,
    /// a 0x4000 badge bit if one is ever present rides along unmasked -- D4), truncated to
    /// <paramref name="capacityWords"/> - 1 ids, then the marker appended. No zero-fill: the
    /// caller (<see cref="OldBlockImage"/>) owns padding out to the old block's own span.</summary>
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

    /// <summary>Pure: <paramref name="projection"/> as little-endian words, zero-filled out to
    /// exactly <paramref name="spanBytes"/> -- the old block's own fixed size, never exceeded
    /// (T6b: the projection is already truncated to fit, so this never writes past the span).</summary>
    public static byte[] OldBlockImage(ushort[] projection, int spanBytes)
    {
        var bytes = new byte[spanBytes];
        for (int i = 0; i < projection.Length; i++) WriteWord(bytes, i, projection[i]);
        return bytes;
    }

    /// <summary>Pure: the marker rule (D3) plus the 0xFFFF wall (finding 8, crash 1), applied to
    /// <paramref name="span"/> (an old chart block, read verbatim) to produce a
    /// <paramref name="regionBytes"/>-long page region image. Three cases: (a) the span already
    /// carries a 0x00FF marker somewhere inside it -- copied unchanged, wall starts right after the
    /// span's own words (whatever trails the marker inside the span, if anything, is harmless: the
    /// game's own marker-seeking walkers already stop before it); (b) no marker but a zero word --
    /// the marker is written into that zero slot in place, wall starts after the span's own words;
    /// (c) every word a nonzero non-marker id (the overflowed shape of finding 10) -- the marker is
    /// appended one word past the span, wall starts right after THAT marker word.</summary>
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

    /// <summary>The serialize edge (D4): no-op when not installed. For each chart, read its page
    /// region, project it down to the old block's own capacity, and write that projection into the
    /// old block -- ONE write per chart, never a byte past its span. Called from inside
    /// <see cref="SaveEdgeHooks.SerializeCore"/>'s own try, before the original runs.</summary>
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

    /// <summary>The load edge (D4): no-op when not installed. For each chart, read the old block
    /// the game just restored from the save struct and write it, marker-ruled and walled, onto the
    /// page -- ONE write per chart. Called from <see cref="SaveEdgeHooks.AfterApply"/> right after
    /// <see cref="SaveEdgeHooks.ReadHeader"/> succeeds and before the bag/template replay, so the
    /// seat that follows always lands on a fresh page.</summary>
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
