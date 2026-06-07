namespace LivingWeapon;

/// <summary>
/// The slice of memory access the gameplay logic needs, behind an interface so the
/// logic (kill attribution, etc.) is unit-testable with a fake memory -- no live game.
/// LiveMemory is the production adapter over the RPM/WPM-backed <see cref="Mem"/>.
/// </summary>
internal interface IGameMemory
{
    byte U8(long addr);
    ushort U16(long addr);
}

internal sealed class LiveMemory : IGameMemory
{
    public byte U8(long addr) => Mem.U8(addr);
    public ushort U16(long addr) => Mem.U16(addr);
}
