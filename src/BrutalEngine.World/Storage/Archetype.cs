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
/// <b>Edges.</b> <see cref="WithAdded"/> and <see cref="WithRemoved"/> find the neighbour that adds or removes one
/// component. The first call per component asks the set, which finds or creates the neighbour, and caches the
/// neighbour's index in <see cref="Edges"/>; later calls read the cache only. Archetypes are never destroyed, so a
/// cached edge stays right however many archetypes are created later.
/// </para>
/// <para>
/// <b>Memory.</b> The archetype owns its chunk table and its edge table, which hold no native memory until a chunk is
/// added or an edge is cached; <see cref="Dispose"/> returns the table's blocks to the world's pool and frees both
/// tables' arrays. Creating an archetype allocates its managed metadata once (setup work); reading its members and
/// following a cached edge allocate nothing.
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
        Edges = new ArchetypeEdges(set.Allocator);
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

    /// <summary>The archetype's cached add and remove edges: component index to neighbour index.</summary>
    public ArchetypeEdges Edges { get; }

    /// <summary>Returns the archetype with this one's components plus <paramref name="component"/>.</summary>
    /// <param name="component">A registered component this archetype lacks.</param>
    /// <returns>The neighbour, found through the cached edge, or through the set (created if needed) and then cached.</returns>
    /// <exception cref="InvalidOperationException">
    /// The archetype already has <paramref name="component"/> (internal misuse), or the neighbour is new and its row is
    /// too wide. Nothing is cached.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="component"/> isn't registered.</exception>
    /// <exception cref="ObjectDisposedException">The world's archetype set is disposed.</exception>
    /// <exception cref="OutOfMemoryException">
    /// The edge table can't grow. The neighbour exists in the set, the edge table is unchanged, and the next call
    /// looks the neighbour up again and caches it.
    /// </exception>
    /// <remarks>Allocates nothing when the edge is cached.</remarks>
    public Archetype WithAdded(ComponentId component) => Neighbour(component.Index, adding: true);

    /// <summary>Returns the archetype with this one's components minus <paramref name="component"/>.</summary>
    /// <param name="component">A component this archetype has.</param>
    /// <returns>The neighbour, found through the cached edge, or through the set (created if needed) and then cached.</returns>
    /// <exception cref="InvalidOperationException">The archetype lacks <paramref name="component"/> (internal misuse). Nothing is cached.</exception>
    /// <exception cref="ObjectDisposedException">The world's archetype set is disposed.</exception>
    /// <exception cref="OutOfMemoryException">The edge table can't grow, as for <see cref="WithAdded"/>.</exception>
    /// <remarks>Allocates nothing when the edge is cached.</remarks>
    public Archetype WithRemoved(ComponentId component) => Neighbour(component.Index, adding: false);

    /// <summary>
    /// Returns the chunk table's blocks to the pool and frees both tables' arrays. Idempotent. The edge table is freed
    /// even if the chunk table throws.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The world's chunk pool was disposed first (see <see cref="ChunkTable.Dispose"/>).</exception>
    public void Dispose()
    {
        try
        {
            Chunks.Dispose();
        }
        finally
        {
            Edges.Dispose();
        }
    }

    private Archetype Neighbour(int component, bool adding)
    {
        // Membership first: a cached edge for a component is its add edge if the archetype lacks it and its remove
        // edge if it has it, so asking the other direction must never read the cache.
        if (Signature.Contains(component) == adding)
        {
            throw new InvalidOperationException(adding
                ? $"Archetype {Index} already has component {component}; it has no add edge for it."
                : $"Archetype {Index} doesn't have component {component}; it has no remove edge for it.");
        }

        if (component >= 0 && Edges.TryGet(component, out int cached))
        {
            return Set[cached];
        }

        Archetype neighbour = adding ? Set.GetOrCreateAdding(this, component) : Set.GetOrCreateRemoving(this, component);
        Edges.Add(component, neighbour.Index);
        return neighbour;
    }
}
