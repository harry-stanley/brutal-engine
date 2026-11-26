using System;

namespace BrutalEngine.World.Storage;

/// <summary>
/// One archetype: a unique component set, the layout of its chunks and its chunk table (docs/design/world.md,
/// section 4). Created only by its <see cref="ArchetypeSet"/>.
/// </summary>
/// <remarks>
/// <para>
/// Archetypes are append-only: the set never destroys one while its world lives, so <see cref="Index"/> names the same
/// archetype for the world's lifetime, and an archetype without entities keeps its metadata but no chunks.
/// </para>
/// <para>
/// <b>Memory.</b> The archetype owns its chunk table, which holds no native memory until a chunk is added;
/// <see cref="Dispose"/> returns the table's blocks to the world's pool and frees its arrays. Creating an archetype
/// allocates its managed metadata once (setup work); reading its members allocates nothing.
/// </para>
/// <para>Not thread-safe: the owning world's owner thread is its only caller.</para>
/// </remarks>
internal sealed class Archetype : IDisposable
{
    /// <summary>Creates an archetype. Only <see cref="ArchetypeSet"/> calls this, with a layout computed for the signature.</summary>
    /// <param name="set">The set that creates and owns the archetype.</param>
    /// <param name="index">The archetype's index in the set: its creation order, from 0.</param>
    /// <param name="signature">The archetype's components.</param>
    /// <param name="layout">The layout of the archetype's chunks.</param>
    /// <param name="chunks">The archetype's chunk table, with one column per layout column. Owned from here on.</param>
    internal Archetype(ArchetypeSet set, int index, ArchetypeSignature signature, ChunkLayout layout, ChunkTable chunks)
    {
        Set = set;
        Index = index;
        Signature = signature;
        Layout = layout;
        Chunks = chunks;
    }

    /// <summary>The set that created the archetype, through which its neighbours are found.</summary>
    public ArchetypeSet Set { get; }

    /// <summary>The archetype's index in its set: <c>0</c> for the empty archetype, then creation order.</summary>
    public int Index { get; }

    /// <summary>The archetype's components.</summary>
    public ArchetypeSignature Signature { get; }

    /// <summary>The layout of the archetype's chunks: row capacity and column offsets.</summary>
    public ChunkLayout Layout { get; }

    /// <summary>The archetype's chunks and their change ticks.</summary>
    public ChunkTable Chunks { get; }

    /// <summary>Returns the chunk table's blocks to the pool and frees its arrays. Idempotent.</summary>
    /// <exception cref="ObjectDisposedException">The world's chunk pool was disposed first (see <see cref="ChunkTable.Dispose"/>).</exception>
    public void Dispose() => Chunks.Dispose();
}
