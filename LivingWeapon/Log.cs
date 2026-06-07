using System;
using System.IO;

namespace LivingWeapon;

/// <summary>Minimal logger: Reloaded console + a rolling file in the mod dir.</summary>
internal static class Log
{
    private static string? _file;

    public static void Init(string modDir)
    {
        try { _file = Path.Combine(modDir, "livingweapon.log"); } catch { _file = null; }
    }

    public static void Info(string m) => Write("[LivingWeapon] " + m);
    public static void Error(string m) => Write("[LivingWeapon] ERROR: " + m);

    private static void Write(string m)
    {
        try { Console.WriteLine(m); } catch { }
        try { if (_file != null) File.AppendAllText(_file, DateTime.Now.ToString("HH:mm:ss ") + m + "\n"); } catch { }
    }
}
