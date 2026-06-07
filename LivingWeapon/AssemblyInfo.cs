using System.Runtime.CompilerServices;

// Lets the test project drive internal types (KillTracker, IGameMemory, Offsets).
[assembly: InternalsVisibleTo("LivingWeapon.Tests")]
