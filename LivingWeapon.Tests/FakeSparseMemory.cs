using System.Collections.Generic;
using LivingWeapon;

namespace LivingWeapon.Tests;

/// <summary>
/// Sparse address -&gt; value IGameMemory fake shared by the policy/tracker suites
/// (KillTracker, TurnTracker, Wielder, ExtraTurn, Rapture, ...). Unseeded reads
/// return 0, mirroring Mem's fail-safe contract. W8 records the write in Written
/// (so tests can assert exactly what was written) AND updates U8s so read-backs
/// observe it. Writable passes only for explicitly marked addresses -- the slam
/// guard's contract from the ExtraTurn integration suite.
/// </summary>
internal sealed class FakeSparseMemory : IGameMemory
{
    public readonly Dictionary<long, ushort> U16s = new();
    public readonly Dictionary<long, byte>   U8s  = new();
    public readonly HashSet<long> WritableAddrs   = new();
    public readonly Dictionary<long, byte>   Written = new();

    public byte   U8(long a)  => U8s.TryGetValue(a, out var v) ? v : (byte)0;
    public ushort U16(long a) => U16s.TryGetValue(a, out var v) ? v : (ushort)0;
    public bool   Writable(long a, int n) => WritableAddrs.Contains(a);
    public void   W8(long a, byte v) { Written[a] = v; U8s[a] = v; }
}
