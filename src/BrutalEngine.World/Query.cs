using System;
using BrutalEngine.Core;
using BrutalEngine.World.Queries;

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
    /// <see cref="EcsWorld.ChangeTick"/> when the query is built, and the world's clamp raises it like every other
    /// last-run tick (see <see cref="EcsWorld.AdvanceChangeTick"/>). Meaningful once <see cref="HasRun"/> is true.
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
    /// Whether a full enumeration of the query has completed. Until then its change filters match every chunk. No
    /// enumeration exists in this version, so it is always <see langword="false"/>.
    /// </summary>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The query or its world is disposed.</exception>
    /// <remarks>Allocates nothing.</remarks>
    public bool HasRun
    {
        get
        {
            ThrowIfUnusable("Query.HasRun");

            // TODO(#341): the first completed chunk enumeration sets it.
            return false;
        }
    }

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

    private void ThrowIfUnusable(string apiName)
    {
        _world.ThrowIfUnusableFor(apiName);
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
