using System.IO;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>LW-147: pins TempDirs' contract (see its own doc for why this fixture exists).</summary>
public class TempDirsTests
{
    [Fact]
    public void Create_makes_a_fresh_existing_directory()
    {
        using var t = TempDirs.Create("lw_tempdirstest_");
        Assert.True(Directory.Exists(t.Dir));
        Assert.StartsWith("lw_tempdirstest_", Path.GetFileName(t.Dir));
    }

    [Fact]
    public void Two_Create_calls_never_collide()
    {
        using var a = TempDirs.Create("lw_tempdirstest_");
        using var b = TempDirs.Create("lw_tempdirstest_");
        Assert.NotEqual(a.Dir, b.Dir);
    }

    [Fact]
    public void Dispose_deletes_the_directory_recursively()
    {
        var t = TempDirs.Create("lw_tempdirstest_");
        File.WriteAllText(Path.Combine(t.Dir, "leftover.txt"), "content");

        t.Dispose();

        Assert.False(Directory.Exists(t.Dir));
    }

    [Fact]
    public void Dispose_does_not_throw_when_the_directory_is_already_gone()
    {
        var t = TempDirs.Create("lw_tempdirstest_");
        Directory.Delete(t.Dir, recursive: true);

        var ex = Record.Exception(() => t.Dispose());

        Assert.Null(ex);
    }
}
