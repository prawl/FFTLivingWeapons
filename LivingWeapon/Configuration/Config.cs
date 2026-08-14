namespace LivingWeapon.Configuration;

/// <summary>
/// Mod configuration, currently EMPTY on purpose.
///
/// LW-52 removed the BannerToasts, DevSeedKills and VerboseLog toggles from the launcher so
/// players could not switch off designed behaviour, leaving TreasureAlwaysOn as the sole
/// player-facing setting; LW-10 then removed Treasure Master itself (2026-08-14), which took
/// that setting with it. The type stays because the Reloaded-II configurator is wired to it and
/// because the next real setting belongs here rather than in a new parallel mechanism. An empty
/// settings pane is the honest answer to a mod that has nothing for the player to tune: every
/// remaining behaviour keeps its compiled Tuning default.
/// </summary>
public class Config : Configurable<Config>
{
}
