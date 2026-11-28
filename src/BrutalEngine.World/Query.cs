using System;
using BrutalEngine.Core;
using BrutalEngine.World.Queries;
using BrutalEngine.World.Storage;
using BrutalEngine.World.Ticks;

namespace BrutalEngine.World;

/// <summary>
/// A built query: the archetypes of one world that match its terms, kept in a cache that grows as matching archetypes
/// are created (docs/design/world.md, section 5). Made by <see cref="QueryBuilder.Build"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Matching.</b> An archetype matches when it has every required component (terms <c>Read</c>, <c>Write</c>,
/// <c>With</c>, <c>Changed</c> and <c>Added</c>) and no excluded one (<c>Without</c>). Optional terms don't affect
/// matching.
/// </para>
/// <para>
/// <b>Caching.</b> The query keeps an unmanaged list of its matching archetypes, with each data term's column and column
/// offset per archetype. Archetypes are never destroyed and never change their components, so every refresh examines
/// only the archetypes created since the previous one. The first refresh happens at the query's first use, not at
/// <see cref="QueryBuilder.Build"/>.
/// </para>
/// <para>
/// <b>Enumeration.</b> <c>foreach (ChunkView chunk in query)</c> yields the matching chunks that pass the query's
/// <c>Changed</c> and <c>Added</c> filters, with span access to the declared columns (see <see cref="ChunkEnumerator"/>
/// and <see cref="ChunkView"/>). A completed enumeration sets <see cref="LastRunTick"/> and <see cref="HasRun"/> and
/// advances the world's change tick.
/// </para>
/// <para>
/// <b>Lifetime and ownership.</b> The world owns the query. <see cref="Dispose"/> releases it early; disposing the world
/// releases every query it still holds. The cache comes from the world's allocator. Afterwards every member but
/// <see cref="Dispose"/> throws <see cref="ObjectDisposedException"/>.
/// </para>
/// <para>
/// <b>Threading.</b> Every member, <see cref="Dispose"/> included, is for the world's owner thread and throws
/// <see cref="WrongThreadException"/> naming the member on any other thread.
/// </para>
/// </remarks>
public sealed class Query : IDisposable
{
    private readonly EcsWorld _world;
    private readonly QueryTerms _terms;
    private readonly ArchetypeMatchCache _cache;
    private readonly int _tickSlot;
    private readonly bool _hasFilters;
    private bool _hasRun;
    private bool _disposed;

    /// <summary>
    /// Creates a query with an empty cache and registers its last-run tick with the world's tick consumers, at the
    /// world's change tick. The caller links it into the world's live queries.
    /// </summary>
    /// <exception cref="OutOfMemoryException">
    /// The allocator can't grow the tick-consumer list. Nothing is registered, and the empty cache holds no native memory.
    /// </exception>
    internal Query(EcsWorld world, QueryTerms terms)
    {
        _world = world;
        _terms = terms;
        _cache = new ArchetypeMatchCache(world.Archetypes, terms);
        _tickSlot = world.TickConsumers.Register(world.ChangeTick);
        for (int term = 0; term < terms.DataTermCount; term++)
        {
            _hasFilters |= (terms.GetDataTerm(term).Flags & (TermFlags.Changed | TermFlags.Added)) != 0;
        }
    }

    /// <summary>The world the query belongs to.</summary>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The query or its world is disposed.</exception>
    /// <remarks>Allocates nothing.</remarks>
    public EcsWorld World
    {
        get
        {
            ThrowIfUnusable("Query.World");
            return _world;
        }
    }

    /// <summary>
    /// The query's last-run tick, which its change filters compare chunk ticks against. It starts at the world's
    /// <see cref="EcsWorld.ChangeTick"/> when the query is built, becomes the change tick current when an enumeration
    /// completes (before the world advances it), and the world's clamp raises it like every other last-run tick (see
    /// <see cref="EcsWorld.AdvanceChangeTick"/>). Meaningful once <see cref="HasRun"/> is true.
    /// </summary>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The query or its world is disposed.</exception>
    /// <remarks>Allocates nothing.</remarks>
    public uint LastRunTick
    {
        get
        {
            ThrowIfUnusable("Query.LastRunTick");
            return _world.TickConsumers.Get(_tickSlot);
        }
    }

    /// <summary>
    /// Whether an enumeration of the query has completed: <see cref="ChunkEnumerator.MoveNext"/> returned
    /// <see langword="false"/> at the end. Until then its change filters match every chunk, whatever the ticks, and
    /// <see cref="ChunkView.Changed{T}"/> and <see cref="ChunkView.Added{T}"/> return <see langword="true"/>: a flag,
    /// not a baseline tick, so wrap-around can't defeat it. Never turns back to <see langword="false"/>.
    /// </summary>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The query or its world is disposed.</exception>
    /// <remarks>Allocates nothing.</remarks>
    public bool HasRun
    {
        get
        {
            ThrowIfUnusable("Query.HasRun");
            return _hasRun;
        }
    }

    /// <summary><see cref="HasRun"/> without the checks, for the enumerator and its views.</summary>
    internal bool HasRunCore => _hasRun;

    /// <summary><see cref="LastRunTick"/> without the checks, for the enumerator and its views.</summary>
    internal uint LastRunTickCore => _world.TickConsumers.Get(_tickSlot);

    /// <summary>The world, without the checks.</summary>
    internal EcsWorld Owner => _world;

    /// <summary>The query's terms in matching form.</summary>
    internal QueryTerms Terms => _terms;

    /// <summary>The query's match cache, which it owns and frees when it is released.</summary>
    internal ArchetypeMatchCache Cache => _cache;

    /// <summary>The query's slot in the world's tick consumers.</summary>
    internal int TickSlot => _tickSlot;

    /// <summary>The previous (newer) query in the world's live list, or <see langword="null"/> for the first.</summary>
    internal Query? Previous { get; set; }

    /// <summary>The next (older) query in the world's live list, or <see langword="null"/> for the last.</summary>
    internal Query? Next { get; set; }

    /// <summary>
    /// Counts the entities in the matching archetypes. <c>Changed</c> and <c>Added</c> terms count only as required
    /// components here: their chunk filters are ignored.
    /// </summary>
    /// <returns>The number of matching rows.</returns>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The query or its world is disposed.</exception>
    /// <exception cref="OutOfMemoryException">
    /// The allocator can't grow the cache while it refreshes. The archetypes cached before the failure stay cached, and
    /// the next call examines the rest again.
    /// </exception>
    /// <remarks>
    /// Refreshes the cache first, so archetypes created since the last refresh are counted. Allocates no managed
    /// memory, the refresh included; a refresh that finds more matching archetypes than the cache holds grows it through
    /// the world's allocator. Takes time proportional to the new archetypes and to the matching archetypes.
    /// </remarks>
    public int CountEntities()
    {
        ThrowIfUnusable("Query.CountEntities");
        _cache.Refresh();
        int count = 0;
        for (int entry = 0; entry < _cache.Count; entry++)
        {
            count += _cache.GetArchetype(entry).RowCount;
        }

        return count;
    }

    /// <summary>
    /// Starts an enumeration of the matching chunks: <c>foreach (ChunkView chunk in query)</c>. Refreshes the cache
    /// first, so archetypes created since the last refresh are enumerated.
    /// </summary>
    /// <returns>An enumerator before the first chunk. Dispose it once, and don't copy it.</returns>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The query or its world is disposed.</exception>
    /// <exception cref="OutOfMemoryException">
    /// The allocator can't grow the cache while it refreshes, as for <see cref="CountEntities"/>. No enumeration starts.
    /// </exception>
    /// <remarks>Allocates no managed memory, the refresh included.</remarks>
    public ChunkEnumerator GetEnumerator()
    {
        ThrowIfUnusable("Query.GetEnumerator");
        _cache.Refresh();
        return new ChunkEnumerator(this);
    }

    /// <summary>
    /// Returns whether a chunk passes the query's change filters: before the first completed enumeration every chunk
    /// does; after it, a chunk does when every filtered component's tick there is newer than the last-run tick. A tag
    /// has no ticks, so a filter on one never holds after the first run.
    /// </summary>
    /// <param name="entry">The archetype's cache entry.</param>
    /// <param name="archetype">The archetype in that entry.</param>
    /// <param name="chunk">One of its chunks.</param>
    /// <returns>Whether the enumerator yields the chunk.</returns>
    internal bool PassesFilters(int entry, Archetype archetype, int chunk)
    {
        if (!_hasFilters || !_hasRun)
        {
            return true;
        }

        uint since = LastRunTickCore;
        for (int term = 0; term < _terms.DataTermCount; term++)
        {
            TermFlags flags = _terms.GetDataTerm(term).Flags;
            if ((flags & (TermFlags.Changed | TermFlags.Added)) == 0)
            {
                continue;
            }

            int column = _cache.GetColumn(entry, term);
            if (column < 0
                || ((flags & TermFlags.Changed) != 0 && !TickMath.IsNewer(archetype.Chunks.GetChanged(chunk)[column], since))
                || ((flags & TermFlags.Added) != 0 && !TickMath.IsNewer(archetype.Chunks.GetAdded(chunk)[column], since)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Completes an enumeration: the last-run tick becomes the current change tick, the query has run, and then the
    /// world advances its change tick, so the loop's own writes aren't newer than the last run and later ones are.
    /// </summary>
    internal void CompleteRun()
    {
        _world.TickConsumers.Set(_tickSlot, _world.ChangeTick);
        _hasRun = true;
        _world.Advance();
    }

    /// <summary>
    /// Releases the query: frees its cache, releases its last-run tick slot (a later query reuses it) and removes it from
    /// the world. Idempotent, and does nothing once the world is disposed, which has released the query already.
    /// </summary>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread. Nothing changes.</exception>
    /// <remarks>
    /// Allocates no managed memory. If the allocator's free throws, the exception propagates and the query is still
    /// released and disposed.
    /// </remarks>
    public void Dispose()
    {
        _world.ThrowIfNotOwnerThread("Query.Dispose");
        if (_disposed)
        {
            return;
        }

        // Unlink and release the slot first, so the query is gone from the world even if the cache's free throws.
        _disposed = true;
        _world.RemoveQuery(this);
        _cache.Dispose();
    }

    /// <summary>World disposal: marks the query disposed and frees its cache. The world has unlinked it already.</summary>
    internal void Release()
    {
        _disposed = true;
        _cache.Dispose();
    }

    /// <summary>Checks the owner thread, the world's disposal and the query's, for a member of the query or its enumeration.</summary>
    /// <param name="apiName">The member's name, for the exception message.</param>
    internal void ThrowIfUnusable(string apiName)
    {
        _world.ThrowIfUnusableFor(apiName);
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
