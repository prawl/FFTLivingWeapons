using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Drives the Assert-ReleaseDllHasNoKeyRead function of tools/pipeline.ps1 through a real Windows
/// PowerShell child process over small temp files, so both needles (the UTF-8 P/Invoke name and the
/// UTF-16 string form) and the clean path are pinned without running a real publish.
/// </summary>
public class PublishKeyReadGateTests : IDisposable
{
    private readonly List<TempDirs> _tempDirs = new();

    public void Dispose()
    {
        foreach (var t in _tempDirs) t.Dispose();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docs", "TODO.md")) &&
                Directory.Exists(Path.Combine(dir.FullName, "LivingWeapon")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("repo root (docs/TODO.md + LivingWeapon/) not found above the test bin dir");
    }

    private static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";

    private string WriteBlob(string name, params byte[][] parts)
    {
        var t = TempDirs.Create("lw_keygate_");
        _tempDirs.Add(t);
        string path = Path.Combine(t.Dir, name);
        File.WriteAllBytes(path, parts.SelectMany(p => p).ToArray());
        return path;
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static (int ExitCode, string Stdout, string Stderr) RunGate(string dllPath)
    {
        string pipelinePath = Path.Combine(RepoRoot(), "tools", "pipeline.ps1");
        string command = $". {PsQuote(pipelinePath)}; Assert-ReleaseDllHasNoKeyRead -DllPath {PsQuote(dllPath)}";

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);

        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        Assert.True(proc.WaitForExit(60000), "powershell.exe did not exit within the timeout");
        return (proc.ExitCode, stdout, stderr);
    }

    [Fact]
    public void A_file_without_the_import_passes_silently()
    {
        string dll = WriteBlob("clean.dll", Utf8("MessageBoxW"), new byte[] { 0, 1, 2 }, Utf8("ReadProcessMemory"));
        var r = RunGate(dll);
        Assert.True(r.ExitCode == 0, $"exit {r.ExitCode}. stdout: [{r.Stdout}] stderr: [{r.Stderr}]");
        Assert.Equal("", r.Stderr.Trim());
    }

    [Fact]
    public void The_utf8_import_name_is_refused()
    {
        string dll = WriteBlob("dev8.dll", Utf8("MessageBoxW"), new byte[] { 0, 0 }, Utf8("GetAsyncKeyState"));
        var r = RunGate(dll);
        Assert.True(r.ExitCode != 0, "a UTF-8 import name must make the gate exit non zero");
        Assert.Contains("REFUSING TO PUBLISH", r.Stderr);
    }

    [Fact]
    public void The_utf16_string_form_is_refused()
    {
        string dll = WriteBlob("dev16.dll", Utf8("MessageBoxW"), new byte[] { 0, 0 }, Encoding.Unicode.GetBytes("GetAsyncKeyState"));
        var r = RunGate(dll);
        Assert.True(r.ExitCode != 0, "a UTF-16 string form must make the gate exit non zero");
        Assert.Contains("REFUSING TO PUBLISH", r.Stderr);
    }
}
