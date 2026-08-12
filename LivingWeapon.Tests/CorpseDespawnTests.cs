using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-167 stage 3: CorpseDespawn.cs, the guarded corpse-despawn primitive extracted from
/// BodyDoubleSpike.cs's Ctrl+F5 dev instrument into a production helper for Living Poach. Every
/// test seeds a FakeSparseMemory fixture by hand (not MemSeats: MemSeats writes values but never
/// marks them Readable, and every CorpseDespawn read is guard-gated) so the fail-closed contract
/// is exercised exactly like production: an address that was never marked Readable/Writable must
/// refuse, never throw, never half-act.
///
/// The node's flag byte at node+Offsets.DespawnNodeModeOff (+0x12C) is BYTE-wide (matching
/// BodyDoubleSpike.cs's dev spike, the Proven row's own instrument): seeded straight into
/// FakeSparseMemory.U8s via SeedNode's modeByte parameter, read back via mem.U8(addr), written via
/// mem.W8 -&gt; observable in mem.Written (NOT WrittenU16/WrittenBytes -- see the ONE-WAY assertions
/// below).
///
/// The current-actor node-id global (Offsets.DespawnCurrentActorNodeId) is a genuine dword,
/// unrelated to the node flag byte above: IGameMemory.U32's INTERFACE DEFAULT (GameMemory.cs)
/// composes it from two U16 reads, and FakeSparseMemory does not override U32 -- so it is seeded
/// into U16s (two halves) via SeedCurrentActorNodeId, never U8s alone.
///
/// Guard-refusal fixtures (current-actor, open-turn) mark the node's flag byte WRITABLE before
/// asserting the refusal: with the byte left unmarked, an unrelated Writable-gate failure would
/// produce the same "no write happened" result even if the guard under test were deleted entirely
/// -- a vacuous test. Marking it writable makes the guard under test the ONLY thing standing
/// between the corpse and a write; both fixtures were confirmed RED (test fails, i.e. wrongly
/// passes/writes) by temporarily deleting their guard in CorpseDespawn.cs and restored immediately
/// after (not a permanent test change -- see the session's report for both confirmations).
/// </summary>
public class CorpseDespawnTests
{
    private const int BandSlot = 24;
    private const ushort VictimNameId = 918;
    private const long NodeAddr = 0x2000_1000;
    private const long OtherNodeAddr = 0x2000_2000;

    private static long Frame => Offsets.FrameReadBase + (long)BandSlot * Offsets.CombatStride;

    /// <summary>A live, freshly-dead, unconverted corpse at BandSlot with VictimNameId -- the
    /// state TryDespawn expects at the credit moment. Callers override individual fields to
    /// exercise a refusal.</summary>
    private static long SeedCorpse(FakeSparseMemory mem, bool dead = true, ushort nameId = VictimNameId,
        byte convertMarker = 0, byte turnFlag = 0)
    {
        long entry = Band.Entry(BandSlot);
        mem.U8s[entry + Offsets.ADeadStatus] = dead ? Offsets.ADeadBit : (byte)0;
        mem.MarkReadable(entry + Offsets.ADeadStatus, 1);
        mem.U16s[entry + Offsets.ANameId] = nameId;
        mem.MarkReadable(entry + Offsets.ANameId, 2);
        mem.U8s[entry + Offsets.ACorpseConvertMarker] = convertMarker;
        mem.MarkReadable(entry + Offsets.ACorpseConvertMarker, 1);
        mem.U8s[entry + Offsets.ATurnFlag] = turnFlag;
        mem.MarkReadable(entry + Offsets.ATurnFlag, 1);
        return entry;
    }

    /// <summary>Seed the current-actor node-id dword, readable through IGameMemory's interface-
    /// default U32 (two U16 reads) -- see the class doc.</summary>
    private static void SeedCurrentActorNodeId(FakeSparseMemory mem, uint value)
    {
        mem.U16s[Offsets.DespawnCurrentActorNodeId] = (ushort)(value & 0xFFFF);
        mem.U16s[Offsets.DespawnCurrentActorNodeId + 2] = (ushort)((value >> 16) & 0xFFFF);
        mem.MarkReadable(Offsets.DespawnCurrentActorNodeId, 4);
    }

    /// <summary>Link one node into the render-node list at Offsets.DespawnNodeListHead (a
    /// single-node list unless the caller links more with SeedU64/MarkReadable directly). Marks
    /// the node's whole prefix (through the combat back-pointer) readable, matching TryResolveNode's
    /// single guarded read of that span.</summary>
    private static void SeedHeadNode(FakeSparseMemory mem, long node)
    {
        mem.SeedU64(Offsets.DespawnNodeListHead, (ulong)node);
        mem.MarkReadable(Offsets.DespawnNodeListHead, 8);
    }

    /// <summary>Seed one render node. modeByte is the node's flag byte at +0x12C (BYTE-wide,
    /// matching production); the node's prefix-readable span through the combat back-pointer
    /// already covers +0x12C (0x12C &lt; DespawnNodeCombatOff+8), so no separate MarkReadable call
    /// is needed for it -- only MarkWritable, and only when a test needs the write to succeed.</summary>
    private static void SeedNode(FakeSparseMemory mem, long node, long next, byte id, long combatBackref, byte modeByte = 0)
    {
        mem.SeedU64(node, (ulong)next);   // next pointer at node+0x00
        mem.U8s[node + Offsets.DespawnNodeIdOff] = id;
        mem.SeedU64(node + Offsets.DespawnNodeCombatOff, (ulong)combatBackref);
        mem.U8s[node + Offsets.DespawnNodeModeOff] = modeByte;
        mem.MarkReadable(node, Offsets.DespawnNodeCombatOff + 8);
    }

    private static void SeedNotCurrentActor(FakeSparseMemory mem)
        => SeedCurrentActorNodeId(mem, 0xFFFFFFFF);

    // ── Staleness refusals (before any node read) ──────────────────────────────

    [Fact]
    public void TryDespawn_alive_refuses_before_any_node_read()
    {
        var mem = new FakeSparseMemory();
        SeedCorpse(mem, dead: false);
        SeedHeadNode(mem, NodeAddr);   // a node IS linked; must never be consulted

        bool result = CorpseDespawn.TryDespawn(mem, BandSlot, VictimNameId);

        Assert.False(result);
        Assert.False(mem.ReadCount.ContainsKey(Offsets.DespawnNodeListHead));
        Assert.Empty(mem.Written);
    }

    [Fact]
    public void TryDespawn_nameId_mismatch_refuses_before_any_node_read()
    {
        var mem = new FakeSparseMemory();
        SeedCorpse(mem, nameId: (ushort)(VictimNameId + 1));
        SeedHeadNode(mem, NodeAddr);

        bool result = CorpseDespawn.TryDespawn(mem, BandSlot, VictimNameId);

        Assert.False(result);
        Assert.False(mem.ReadCount.ContainsKey(Offsets.DespawnNodeListHead));
        Assert.Empty(mem.Written);
    }

    [Fact]
    public void TryDespawn_already_converted_to_chest_refuses_before_any_node_read()
    {
        var mem = new FakeSparseMemory();
        SeedCorpse(mem, convertMarker: 1);
        SeedHeadNode(mem, NodeAddr);

        bool result = CorpseDespawn.TryDespawn(mem, BandSlot, VictimNameId);

        Assert.False(result);
        Assert.False(mem.ReadCount.ContainsKey(Offsets.DespawnNodeListHead));
        Assert.Empty(mem.Written);
    }

    // ── Node resolve ────────────────────────────────────────────────────────────

    [Fact]
    public void TryDespawn_no_backref_match_refuses()
    {
        var mem = new FakeSparseMemory();
        SeedCorpse(mem);
        SeedHeadNode(mem, NodeAddr);
        SeedNode(mem, NodeAddr, next: 0, id: 5, combatBackref: Frame + 0x200);   // wrong frame

        bool result = CorpseDespawn.TryDespawn(mem, BandSlot, VictimNameId);

        Assert.False(result);
        Assert.Empty(mem.Written);
    }

    [Fact]
    public void TryDespawn_backref_match_walks_past_a_non_matching_node_to_the_right_one()
    {
        var mem = new FakeSparseMemory();
        SeedCorpse(mem);
        SeedNotCurrentActor(mem);
        SeedHeadNode(mem, NodeAddr);
        SeedNode(mem, NodeAddr, next: OtherNodeAddr, id: 5, combatBackref: Frame + 0x200);   // miss
        SeedNode(mem, OtherNodeAddr, next: 0, id: 6, combatBackref: Frame, modeByte: 0x00);  // hit
        mem.MarkWritable(OtherNodeAddr + Offsets.DespawnNodeModeOff, 1);

        bool result = CorpseDespawn.TryDespawn(mem, BandSlot, VictimNameId);

        Assert.True(result);
        Assert.True(mem.Written.ContainsKey(OtherNodeAddr + Offsets.DespawnNodeModeOff));
        Assert.False(mem.Written.ContainsKey(NodeAddr + Offsets.DespawnNodeModeOff));
    }

    [Fact]
    public void TryDespawn_looped_list_terminates_and_refuses()
    {
        var mem = new FakeSparseMemory();
        SeedCorpse(mem);
        SeedHeadNode(mem, NodeAddr);
        // A 2-node cycle, neither backreferencing the corpse's frame: must not hang.
        SeedNode(mem, NodeAddr, next: OtherNodeAddr, id: 1, combatBackref: Frame + 0x400);
        SeedNode(mem, OtherNodeAddr, next: NodeAddr, id: 2, combatBackref: Frame + 0x600);

        bool result = CorpseDespawn.TryDespawn(mem, BandSlot, VictimNameId);

        Assert.False(result);
        Assert.Empty(mem.Written);
    }

    // ── Guard refusals (node flag byte marked WRITABLE so the guard under test is the ONLY
    //    thing stopping the write -- see the class doc's non-vacuity note) ─────────────────────

    [Fact]
    public void TryDespawn_current_actor_node_refuses()
    {
        var mem = new FakeSparseMemory();
        SeedCorpse(mem);
        SeedHeadNode(mem, NodeAddr);
        SeedNode(mem, NodeAddr, next: 0, id: 7, combatBackref: Frame);
        SeedCurrentActorNodeId(mem, 7);   // matches the node's id byte
        mem.MarkWritable(NodeAddr + Offsets.DespawnNodeModeOff, 1);

        bool result = CorpseDespawn.TryDespawn(mem, BandSlot, VictimNameId);

        Assert.False(result);
        Assert.Empty(mem.Written);
    }

    [Fact]
    public void TryDespawn_open_turn_refuses()
    {
        var mem = new FakeSparseMemory();
        SeedCorpse(mem, turnFlag: 1);
        SeedNotCurrentActor(mem);
        SeedHeadNode(mem, NodeAddr);
        SeedNode(mem, NodeAddr, next: 0, id: 9, combatBackref: Frame);
        mem.MarkWritable(NodeAddr + Offsets.DespawnNodeModeOff, 1);

        bool result = CorpseDespawn.TryDespawn(mem, BandSlot, VictimNameId);

        Assert.False(result);
        Assert.Empty(mem.Written);
    }

    // ── In-flight refusal: a removal already marked on this node must never be stomped ─────────

    [Fact]
    public void TryDespawn_node_already_marked_mode_0x10_refuses_no_write()
    {
        var mem = new FakeSparseMemory();
        SeedCorpse(mem);
        SeedNotCurrentActor(mem);
        SeedHeadNode(mem, NodeAddr);
        SeedNode(mem, NodeAddr, next: 0, id: 3, combatBackref: Frame, modeByte: 0x10);
        mem.MarkWritable(NodeAddr + Offsets.DespawnNodeModeOff, 1);   // guards pass; only the in-flight check should refuse

        bool result = CorpseDespawn.TryDespawn(mem, BandSlot, VictimNameId);

        Assert.False(result);
        Assert.Empty(mem.Written);
    }

    [Fact]
    public void TryDespawn_node_already_marked_mode_0x20_refuses_no_write()
    {
        var mem = new FakeSparseMemory();
        SeedCorpse(mem);
        SeedNotCurrentActor(mem);
        SeedHeadNode(mem, NodeAddr);
        SeedNode(mem, NodeAddr, next: 0, id: 3, combatBackref: Frame, modeByte: 0x20);
        mem.MarkWritable(NodeAddr + Offsets.DespawnNodeModeOff, 1);

        bool result = CorpseDespawn.TryDespawn(mem, BandSlot, VictimNameId);

        Assert.False(result);
        Assert.Empty(mem.Written);
    }

    // ── The write shape ─────────────────────────────────────────────────────────

    [Fact]
    public void TryDespawn_write_clears_bits_0x30_and_sets_0x20_exactly()
    {
        var mem = new FakeSparseMemory();
        SeedCorpse(mem);
        SeedNotCurrentActor(mem);
        SeedHeadNode(mem, NodeAddr);
        // 0xCD has bits 0x30 clear (no in-flight conflict) but other bits set, so the assertion
        // below proves both halves of the mask: unrelated bits preserved, 0x20 added.
        SeedNode(mem, NodeAddr, next: 0, id: 3, combatBackref: Frame, modeByte: 0xCD);
        mem.MarkWritable(NodeAddr + Offsets.DespawnNodeModeOff, 1);

        bool result = CorpseDespawn.TryDespawn(mem, BandSlot, VictimNameId);

        Assert.True(result);
        byte expected = (byte)((0xCD & ~0x30) | 0x20);
        Assert.Equal(expected, mem.Written[NodeAddr + Offsets.DespawnNodeModeOff]);
    }

    [Fact]
    public void TryDespawn_unwritable_flag_byte_refuses_no_write_no_throw()
    {
        var mem = new FakeSparseMemory();
        SeedCorpse(mem);
        SeedNotCurrentActor(mem);
        SeedHeadNode(mem, NodeAddr);
        SeedNode(mem, NodeAddr, next: 0, id: 3, combatBackref: Frame);
        // deliberately NOT marked writable

        var ex = Record.Exception(() => CorpseDespawn.TryDespawn(mem, BandSlot, VictimNameId));

        Assert.Null(ex);
        Assert.Empty(mem.Written);
    }

    // ── ONE-WAY: the write-set is exactly the one flag byte, or empty ──────────

    [Fact]
    public void TryDespawn_success_writes_exactly_the_one_flag_byte()
    {
        var mem = new FakeSparseMemory();
        SeedCorpse(mem);
        SeedNotCurrentActor(mem);
        SeedHeadNode(mem, NodeAddr);
        SeedNode(mem, NodeAddr, next: 0, id: 3, combatBackref: Frame);
        mem.MarkWritable(NodeAddr + Offsets.DespawnNodeModeOff, 1);

        bool result = CorpseDespawn.TryDespawn(mem, BandSlot, VictimNameId);

        Assert.True(result);
        Assert.Single(mem.Written);
        Assert.True(mem.Written.ContainsKey(NodeAddr + Offsets.DespawnNodeModeOff));
        Assert.Empty(mem.WrittenU16);       // no W16 ever fired
        Assert.Empty(mem.WrittenBytes);     // no WriteBytes ever fired
    }
}
