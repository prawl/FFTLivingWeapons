namespace LivingWeapon;

/// <summary>
/// TRANSITIONAL shim -- delegates the retired static Log surface to ModLogger so the PRE-REWORK
/// Puppeteer code still committed at this point in history (it calls Log.Info/Log.Error; its
/// replacement lands as its own later commit after live verification) compiles against this
/// commit's tree. The working tree already uses ModLogger everywhere; nothing in it references
/// this class. DELETE this file in the Puppeteer expiry commit (the rename rides that commit).
/// </summary>
internal static class Log
{
    public static void Info(string m) => ModLogger.Log(m);
    public static void Error(string m) => ModLogger.LogError(m);
}
