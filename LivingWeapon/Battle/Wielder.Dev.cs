using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>Dev diagnostics for Wielder, kept out of the locate file.</summary>
internal static partial class Wielder
{
    /// <summary>DEV diagnostic. On a Locate miss, dump every valid band entry's locate fields next to
    /// what was wanted, so one log read names the rejecting predicate. Dev-pulse callers only.</summary>
    public static void DumpCandidates(IGameMemory mem, IReadOnlyList<int> hands, (int lvl, int br, int fa) fp)
    {
        ModLogger.Debug(LogVerb.Trace, $"wielder search missed this pulse: wanted weapon id in [{string.Join(",", hands)}], brave {fp.br}, faith {fp.fa}; candidates follow");
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (!Band.IsValid(mem, e)) continue;
            ModLogger.Debug(LogVerb.Trace, $"  candidate slot {s}: weapon {mem.U16(e + EntryWeapon)} (wanted one of [{string.Join(",", hands)}]), " +
                     $"level {mem.U8(e + Offsets.ALevel)}, brave {mem.U8(e + Offsets.ABrave)}, faith {mem.U8(e + Offsets.AFaith)}, " +
                     $"position ({mem.U8(e + Offsets.AGx)},{mem.U8(e + Offsets.AGy)})");
        }
    }
}
