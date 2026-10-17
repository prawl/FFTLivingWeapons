namespace LivingWeapon;

/// <summary>
/// One read of a victim's nameId, job byte and undead status bit at a band-entry address. Shared by
/// VictimProbe (Reliquary Phase 0 P1 log-only probe) and the Phase 1 deed-capture stamp in
/// KillTracker.Corpses.cs (docs/RELIQUARY_AC.md). Job 0 is a valid snapshot (Archetype.Unknown); Has
/// depends only on the nameId read, see <see cref="VictimReader.Read"/>.
/// </summary>
internal readonly record struct VictimSnapshot(bool Has, ushort NameId, byte Job, bool Undead);

/// <summary>Shared victim-identity reader (see <see cref="VictimSnapshot"/>'s doc comment).</summary>
internal static class VictimReader
{
    /// <summary>Guarded read of the three fields. Each is read independently and an unreadable one
    /// comes back zero (same shape as Puppeteer.cs:215). Has is true only when the nameId read
    /// succeeded, since both consumers key on it. Same read as the original VictimProbe.Read
    /// (docs/RELIQUARY_AC.md P1).</summary>
    public static VictimSnapshot Read(IGameMemory mem, long addr)
    {
        bool nameOk = mem.Readable(addr + Offsets.ANameId, 2);
        ushort nameId = nameOk ? mem.U16(addr + Offsets.ANameId) : (ushort)0;
        byte job = mem.Readable(addr + Offsets.AJob, 1) ? mem.U8(addr + Offsets.AJob) : (byte)0;
        bool undead = mem.Readable(addr + Offsets.ADeadStatus, 1)
            && (mem.U8(addr + Offsets.ADeadStatus) & Offsets.AUndeadBit) != 0;
        return new VictimSnapshot(nameOk, nameId, job, undead);
    }
}
