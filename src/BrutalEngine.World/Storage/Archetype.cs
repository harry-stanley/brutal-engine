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
/// <b>Rows.</b> <see cref="AppendRow"/> and <see cref="SwapRemove"/> keep the dense invariant: every chunk but the
/// last is full, and rows <c>0</c> to count - 1 of each chunk are live, so iteration never checks liveness. A removal
/// moves the archetype's last row into the hole, and a last chunk that empties goes back to the world's pool.
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
internal sealed unsafe class Archetype : IDisposable
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

    /// <summary>The number of rows in the archetype's chunks: its entities. <c>0</c> once disposed.</summary>
    public int RowCount { get; private set; }

    /// <summary>
    /// Appends a row for <paramref name="entity"/>: in the last chunk if it has room, otherwise in a new chunk. Writes
    /// the entity id; the row's column values are uninitialised, and the caller writes them.
    /// </summary>
    /// <param name="entity">The entity the row stores. Not <see cref="Entity.Null"/>.</param>
    /// <param name="tick">The world's current change tick, which a new chunk's ticks start at.</param>
    /// <returns>Where the row is: its chunk index and its row within the chunk.</returns>
    /// <exception cref="ArgumentException"><paramref name="entity"/> is <see cref="Entity.Null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The archetype is disposed.</exception>
    /// <exception cref="OutOfMemoryException">
    /// A new chunk is needed and the allocator can't provide it or the grown chunk arrays. The archetype is unchanged.
    /// </exception>
    /// <remarks>
    /// Allocates no managed memory. A new chunk comes from the pool, which asks the allocator only when it holds none.
    /// </remarks>
    public (int Chunk, int Row) AppendRow(Entity entity, uint tick)
    {
        if (entity.IsNull)
        {
            throw new ArgumentException("A row can't store Entity.Null.", nameof(entity));
        }

        int chunk = Chunks.ChunkCount - 1;
        if (chunk < 0 || Chunks.GetCount(chunk) == Layout.Capacity)
        {
            // The only call that can fail, and it leaves the table unchanged when it does.
            chunk = Chunks.AddChunk(tick);
        }

        int row = Chunks.GetCount(chunk);

        // Sound: the entity ids come first in a chunk, Layout.Capacity of them, and row < Layout.Capacity: a chunk
        // with Capacity rows is full and gets no more. The block is 64-byte aligned, so the 8-byte write is aligned.
        ((Entity*)Chunks.GetData(chunk))[row] = entity;
        Chunks.SetCount(chunk, row + 1);
        RowCount++;
        return (chunk, row);
    }

    /// <summary>
    /// Removes a row by moving the archetype's last row into it, then shrinks the last chunk, returning it to the pool
    /// if it empties. The caller updates the moved entity's record and stamps the <c>Changed</c> ticks of
    /// <paramref name="chunk"/>, which received the moved row.
    /// </summary>
    /// <param name="chunk">The row's chunk, from 0 to the chunk count - 1.</param>
    /// <param name="row">The row within the chunk, from 0 to that chunk's row count - 1.</param>
    /// <returns>
    /// The entity whose row moved into (<paramref name="chunk"/>, <paramref name="row"/>), or <see cref="Entity.Null"/>
    /// if the removed row was the last one and nothing moved.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="chunk"/> or <paramref name="row"/> isn't a live row (a disposed archetype has none). Nothing changes.
    /// </exception>
    /// <remarks>Allocates nothing and makes no allocator call: an emptied chunk goes back to the pool.</remarks>
    public Entity SwapRemove(int chunk, int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chunk);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(chunk, Chunks.ChunkCount);
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, Chunks.GetCount(chunk));

        int last = Chunks.ChunkCount - 1;
        int lastRow = Chunks.GetCount(last) - 1;
        Entity moved = Entity.Null;
        if (chunk != last || row != lastRow)
        {
            byte* target = Chunks.GetData(chunk);
            byte* source = Chunks.GetData(last);

            // Sound: both rows are live rows of chunks of this layout (checked above), so the entity id and every
            // column value of each lie inside its block, and two different rows never overlap.
            moved = ((Entity*)source)[lastRow];
            ((Entity*)target)[row] = moved;
            ReadOnlySpan<ComponentType> columns = Layout.Columns;
            ReadOnlySpan<int> offsets = Layout.ColumnOffsets;
            for (int column = 0; column < columns.Length; column++)
            {
                int size = columns[column].Size;
                Buffer.MemoryCopy(source + offsets[column] + ((nint)lastRow * size), target + offsets[column] + ((nint)row * size), size, size);
            }
        }

        Chunks.SetCount(last, lastRow);
        if (lastRow == 0)
        {
            Chunks.RemoveLastChunk();
        }

        RowCount--;
        return moved;
    }

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
    /// Returns the chunk table's blocks to the pool, frees both tables' arrays and sets <see cref="RowCount"/> to
    /// <c>0</c>. Idempotent. The edge table is freed even if the chunk table throws.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The world's chunk pool was disposed first (see <see cref="ChunkTable.Dispose"/>).</exception>
    public void Dispose()
    {
        // First, so the count matches the chunk table, which holds no chunks once disposed, even if a dispose throws.
        RowCount = 0;
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
