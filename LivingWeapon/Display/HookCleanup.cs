using System;
using Reloaded.Hooks.Definitions;

namespace LivingWeapon;

/// <summary>Turns off a hook whose install failed without letting a fault in the turn off escape.</summary>
internal static class HookCleanup
{
    internal static void DisableQuietly<T>(IHook<T>? hook, LogVerb verb, string what) where T : Delegate
    {
        try
        {
            if (hook?.IsHookEnabled == true) hook.Disable();
        }
        catch (Exception ex)
        {
            ModLogger.Debug(verb, what + " disable after a failed install swallowed: " + ex.Message);
        }
    }
}
