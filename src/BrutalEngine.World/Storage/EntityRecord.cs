using System.Runtime.InteropServices;

namespace BrutalEngine.World.Storage;

/// <summary>
/// One slot of the entity table: the slot's current generation and where its entity is stored. 16 bytes, so four
/// records share a cache line.
/// </summary>
/// <remarks>
/// <see cref="Archetype"/> doubles as the slot's state: <c>&gt;= 0</c> is placed (alive, stored at
/// <see cref="Archetype"/>, <see cref="Chunk"/>, <see cref="Row"/>), and the negative values are
/// <see cref="FreeState"/>, <see cref="PendingState"/> and <see cref="RetiredState"/>. A slot that isn't placed has
/// <see cref="Chunk"/> and <see cref="Row"/> set to <c>-1</c>. Plain mutable data, owned by its table.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct EntityRecord
{
    /// <summary>State of a slot in the free-index queue, waiting for reuse.</summary>
    public const int FreeState = -1;

    /// <summary>State of a slot whose entity is issued but not placed in storage yet.</summary>
    public const int PendingState = -2;

    /// <summary>State of a slot freed at generation <see cref="uint.MaxValue"/>: it is never reused.</summary>
    public const int RetiredState = -3;

    /// <summary>The slot's current generation, from 1. An entity is current only while its generation matches.</summary>
    public uint Generation;

    /// <summary>The archetype holding the entity when placed, otherwise one of the state constants.</summary>
    public int Archetype;

    /// <summary>The chunk within the archetype when placed, otherwise <c>-1</c>.</summary>
    public int Chunk;

    /// <summary>The row within the chunk when placed, otherwise <c>-1</c>.</summary>
    public int Row;
}
