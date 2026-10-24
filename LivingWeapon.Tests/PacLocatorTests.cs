using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace LivingWeapon.Tests;

public class PacLocatorTests
{
    private const string Exe = @"X:\Games\FFT\FFT_enhanced.exe";
    private const string ModDir = @"D:\Tools\Reloaded\Mods\prawl.fft.livingweapons";
    private const string ExePac = @"X:\Games\FFT\data\enhanced\modded.pac";
    private const string WalkPac = @"D:\Tools\data\enhanced\modded.pac";

    private static readonly byte[] OldBytes = { 0x01, 0x02, 0x03, 0x04 };
    private static readonly byte[] NewBytes = { 0x11, 0x12, 0x13, 0x14 };

    [Fact]
    public void Candidates_ExeFolderFirst_ThenThreeFolderWalk()
    {
        var c = PacLocator.Candidates(Exe, ModDir);

        Assert.Equal(
            new[] { new PacCandidate(PacLocator.ExeLabel, ExePac), new PacCandidate(PacLocator.ModDirLabel, WalkPac) },
            c.ToArray());
    }

    [Fact]
    public void Candidates_InstallInsideGameFolder_CollapsesToTheExeCandidate()
    {
        var c = PacLocator.Candidates(@"C:\Game\fft_enhanced.exe", @"C:\GAME\Reloaded\Mods\m");

        var only = Assert.Single(c);
        Assert.Equal(PacLocator.ExeLabel, only.Label);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Candidates_NoExePath_KeepsOnlyTheWalk(string? exe)
    {
        var c = PacLocator.Candidates(exe, @"D:\Tools\Reloaded\Mods\m");

        var only = Assert.Single(c);
        Assert.Equal(PacLocator.ModDirLabel, only.Label);
    }

    [Theory]
    [InlineData(@"C:\m")]
    [InlineData("")]
    [InlineData(null)]
    public void Candidates_ShallowModDir_KeepsOnlyTheExe(string? modDir)
    {
        var c = PacLocator.Candidates(@"X:\G\FFT_enhanced.exe", modDir);

        var only = Assert.Single(c);
        Assert.Equal(PacLocator.ExeLabel, only.Label);
    }

    [Fact]
    public void Candidates_NothingUsable_IsEmpty()
    {
        Assert.Empty(PacLocator.Candidates(null, ""));
    }

    [Fact]
    public void Candidates_NulInModDir_SkipsOnlyTheWalk()
    {
        var c = PacLocator.Candidates(Exe, @"D:\Tools\Reloaded\Mods\m" + "\0x");

        var only = Assert.Single(c);
        Assert.Equal(PacLocator.ExeLabel, only.Label);
    }

    [Fact]
    public void Candidates_NulInExePath_SkipsOnlyTheExe()
    {
        var c = PacLocator.Candidates(@"X:\Games\F" + "\0" + @"T\FFT_enhanced.exe", @"D:\Tools\Reloaded\Mods\m");

        var only = Assert.Single(c);
        Assert.Equal(PacLocator.ModDirLabel, only.Label);
    }

    [Fact]
    public void FirstExisting_PicksTheFirstThatExists()
    {
        var c = PacLocator.Candidates(Exe, ModDir);

        Assert.Equal(PacLocator.ModDirLabel, PacLocator.FirstExisting(c, p => p == WalkPac)?.Label);
        Assert.Equal(PacLocator.ExeLabel, PacLocator.FirstExisting(c, _ => true)?.Label);
        Assert.Null(PacLocator.FirstExisting(c, _ => false));
    }

    [Fact]
    public void FirstExisting_ThrowingProbeCountsAsMissing()
    {
        var c = PacLocator.Candidates(Exe, ModDir);

        var hit = PacLocator.FirstExisting(c, p => p == ExePac ? throw new IOException() : p == WalkPac);

        Assert.Equal(PacLocator.ModDirLabel, hit?.Label);
    }

    [Fact]
    public void Describe_NamesEachLabelAndPath()
    {
        Assert.Equal("no candidate path", PacLocator.Describe(new List<PacCandidate>()));
        Assert.Equal(
            $"beside the game exe ({ExePac}); three folders above the mod folder ({WalkPac})",
            PacLocator.Describe(PacLocator.Candidates(Exe, ModDir)));
    }

    private static string WritePac(string root)
    {
        var path = Path.Combine(root, "Game", "data", "enhanced", "modded.pac");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = Enumerable.Repeat((byte)0xAA, 100).ToArray();
        Array.Copy(OldBytes, 0, content, 40, OldBytes.Length);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static IconGlowEntry Entry() =>
        new() { Id = 70, Surface = "card", BaseRel = "x/ei_070.tex", Length = 4 };

    private static IReadOnlyList<(IconGlowEntry Entry, byte[] OldBytes, byte[] NewBytes, byte[]? PriorTarget)> Batch() =>
        new[] { (Entry(), OldBytes, NewBytes, (byte[]?)null) };

    [Fact]
    public void Store_ReloadedOutsideGameFolder_PatchesThePacBesideTheExe()
    {
        using var temp = TempDirs.Create("lw513_");
        var pac = WritePac(temp.Dir);
        var modDir = Path.Combine(temp.Dir, "Elsewhere", "Reloaded", "Mods", "prawl.fft.livingweapons");
        Directory.CreateDirectory(modDir);
        var store = new FileIconGlowStore(modDir, () => Path.Combine(temp.Dir, "Game", "FFT_enhanced.exe"), File.Exists);
        using var cap = LogCapture.Start();

        var r = store.PatchPacBatch(Batch());

        Assert.Equal(PacPatchResult.Patched, r[(70, "card")]);
        Assert.Equal(NewBytes, File.ReadAllBytes(pac).Skip(40).Take(4).ToArray());
        Assert.Contains(cap.File, l => l.Contains("[INFO]") && l.EndsWith("IconGlow found modded.pac beside the game exe.", StringComparison.Ordinal));
        Assert.Contains(cap.File, l => l.Contains("[trace]") && l.Contains($"IconGlow modded.pac path {pac}"));
    }

    [Fact]
    public void Store_ExeLookupThrows_FallsBackToTheWalk()
    {
        using var temp = TempDirs.Create("lw513_");
        WritePac(temp.Dir);
        var modDir = Path.Combine(temp.Dir, "Game", "Reloaded", "Mods", "m");
        Directory.CreateDirectory(modDir);
        var store = new FileIconGlowStore(modDir, () => throw new InvalidOperationException(), File.Exists);
        using var cap = LogCapture.Start();

        var r = store.PatchPacBatch(Batch());

        Assert.Equal(PacPatchResult.Patched, r[(70, "card")]);
        Assert.Contains(cap.File, l => l.Contains("[INFO]") && l.EndsWith("IconGlow found modded.pac three folders above the mod folder.", StringComparison.Ordinal));
    }

    [Fact]
    public void Store_NoPacAnywhere_UnavailableOnceAndLatched()
    {
        using var temp = TempDirs.Create("lw513_");
        var modDir = Path.Combine(temp.Dir, "A", "B", "C", "D");
        Directory.CreateDirectory(modDir);
        int calls = 0;
        var store = new FileIconGlowStore(modDir, () => Path.Combine(temp.Dir, "NoGame", "FFT_enhanced.exe"),
            p => { calls++; return File.Exists(p); });
        using var cap = LogCapture.Start();

        var first = store.PatchPacBatch(Batch());

        Assert.Equal(PacPatchResult.Unavailable, first[(70, "card")]);
        Assert.Contains(cap.File, l => l.Contains("IconGlow found no modded.pac at"));
        int seen = calls;
        Assert.True(seen > 0);

        var second = store.PatchPacBatch(Batch());

        Assert.Equal(PacPatchResult.Unavailable, second[(70, "card")]);
        Assert.Equal(seen, calls);
    }
}
