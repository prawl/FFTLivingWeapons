// Opens internals to the test project and the shared logger namespace to every file.
global using ModGates.Logging;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("LivingWeapon.Tests")]
