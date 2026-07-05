namespace LivingWeapon;

/// <summary>
/// One point-in-time read of a victim's three identity fields at a band-entry address:
/// nameId, job byte, and the undead status bit. Shared by VictimProbe (Reliquary Phase 0's
/// P1 log-only probe) and KillTracker.Corpses.cs's Phase 1 deed-capture stamp (Reliquary
/// Phase 1, docs/RELIQUARY_AC.md) so the two never drift -- one guarded read, two consumers.
///
/// Has=true with Job=0 IS a valid snapshot: job 0 classifies as VictimClass.Archetype.Unknown
/// (a real, storable outcome), not a read failure. Has gates on the nameId read alone -- see
/// <see cref="VictimReader.Read"/>'s doc comment for why.
/// </summary>
internal readonly record struct VictimSnapshot(bool Has, ushort NameId, byte Job, bool Undead);

/// <summary>Shared victim-identity reader (see <see cref="VictimSnapshot"/>'s doc comment).</summary>
internal static class VictimReader
{
    /// <summary>Guarded three-field read (mirrors the Puppeteer.cs:215 idiom: Readable-gated, else
    /// the zero value). Each field is read independently so a partially-unreadable slot still
    /// yields whatever WAS readable. Has is true only when the nameId read succeeded -- nameId is
    /// the field both consumers actually key on (job/undead are secondary corroboration), so a
    /// snapshot with a readable job/undead but no nameId is still reported as "nothing sane to
    /// use" rather than manufacturing a false positive from a zeroed nameId. Byte-for-byte the
    /// original VictimProbe.Read (docs/RELIQUARY_AC.md P1), extracted here so the log-only probe
    /// and the Phase 1 behavioral capture can never disagree on what "a sane read" means.</summary>
    public static VictimSnapshot Read(IGameMemory mem, long addr)
    {
        bool nameOk = mem.Readable(addr + Offsets.ANameId, 2);
        ushort nameId = nameOk ? mem.U16(addr + Offsets.ANameId) : (ushort)0;
        byte job = mem.Readable(addr + Puppeteer.JobOff, 1) ? mem.U8(addr + Puppeteer.JobOff) : (byte)0;
        bool undead = mem.Readable(addr + Offsets.ADeadStatus, 1)
            && (mem.U8(addr + Offsets.ADeadStatus) & Offsets.AUndeadBit) != 0;
        return new VictimSnapshot(nameOk, nameId, job, undead);
    }
}
