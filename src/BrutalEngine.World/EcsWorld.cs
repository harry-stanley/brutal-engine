using System;
using System.Numerics;
using BrutalEngine.Core;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Storage;

namespace BrutalEngine.World;

/// <summary>
/// A world: the container of a game's entities and their components. One world is the unit of ownership,
/// threading and disposal for everything stored in it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Owner thread.</b> The thread that constructs the world owns it. Every member, <see cref="Dispose"/> included,
/// checks that it is called on that thread and throws <see cref="WrongThreadException"/> naming the member otherwise.
/// The check is one integer comparison and runs in every build.
/// </para>
/// <para>
/// <b>Ownership.</b> The world owns every unmanaged block it allocates and frees it in <see cref="Dispose"/>. All of
/// it comes from <see cref="WorldOptions.Allocator"/>, which the world does not own: the caller keeps it alive until
/// the world is disposed, and the world never disposes it. Without one, the world uses a
/// <see cref="HeapAllocator"/> of its own.
/// </para>
/// <para>
/// <b>Allocation.</b> Constructing a world allocates; it is setup work, not frame work. It reserves
/// <see cref="WorldOptions.InitialEntityCapacity"/> entity slots from the allocator up front, and the entity table
/// grows by doubling through the same allocator, up to <see cref="WorldOptions.MaxEntities"/>. Its component
/// registry is managed, and grows as types are registered. Its chunk pool allocates nothing until storage first
/// needs a chunk, and keeps emptied chunks for reuse until <see cref="TrimMemory"/> or <see cref="Dispose"/>.
/// Reading <see cref="Name"/> allocates nothing.
/// </para>
/// <para>
/// <b>Entities.</b> <see cref="Spawn()"/> and <see cref="Despawn"/> add and remove entities directly; misuse (a null,
/// stale or pending entity) throws before anything changes. Spawning and despawning allocate no managed memory, and
/// once the entity table and the chunk pool have grown to the churn's size they make no allocator calls either.
/// </para>
/// </remarks>
public sealed partial class EcsWorld : IDisposable
{
    private readonly ThreadAffinity _owner;
    private readonly string _name;
    private readonly INativeAllocator _allocator;
    private readonly EntityTable _entities;
    private readonly ComponentRegistry _components;
    private readonly ChunkPool _chunkPool;
    private readonly ArchetypeSet _archetypes;
    private bool _disposed;

    /// <summary>Creates an empty world owned by the calling thread.</summary>
    /// <param name="options">Settings. The world copies what it needs, so later changes have no effect.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <see cref="WorldOptions.Clock"/> is <see langword="null"/>, or <see cref="WorldOptions.Name"/> is
    /// <see langword="null"/> or empty. The message names the option.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A numeric option is outside the range documented on <see cref="WorldOptions"/>. The message names the option.
    /// </exception>
    /// <exception cref="OutOfMemoryException">The allocator can't provide the initial entity table.</exception>
    public EcsWorld(WorldOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);

        _owner = ThreadAffinity.CurrentThread();
        _name = options.Name;

        // HeapAllocator is stateless and holds nothing to release, so the world's own one needs no disposal.
        _allocator = options.Allocator ?? new HeapAllocator();

        // Managed only, so it needs no release if the entity table below throws.
        _components = new ComponentRegistry(_owner, options.MaxComponentTypes);

        // Allocates nothing until a block is rented, so it needs no release if the entity table below throws.
        _chunkPool = new ChunkPool(_allocator, options.ChunkBytes);

        // Managed only until an archetype gets its first chunk, so it needs no release if the entity table below
        // throws. It starts with the empty archetype at index 0.
        _archetypes = new ArchetypeSet(_allocator, _chunkPool, _components, options.MaxComponentTypes);

        // Last, so nothing allocated here needs releasing if an earlier step throws; the table frees its own partial
        // allocation if it throws.
        _entities = new EntityTable(_allocator, options.InitialEntityCapacity, options.MaxEntities);
    }

    /// <summary>The world's name, from <see cref="WorldOptions.Name"/>, for diagnostics and leak reports.</summary>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>Allocates nothing.</remarks>
    public string Name
    {
        get
        {
            ThrowIfUnusable("EcsWorld.Name");
            return _name;
        }
    }

    /// <summary>The world's component types: registration and the type and stable-id lookups.</summary>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>
    /// The registry belongs to the world: it has the world's owner thread, holds at most
    /// <see cref="WorldOptions.MaxComponentTypes"/> types, and becomes unusable when the world is disposed. Allocates
    /// nothing.
    /// </remarks>
    public ComponentRegistry Components
    {
        get
        {
            ThrowIfUnusable("EcsWorld.Components");
            return _components;
        }
    }

    /// <summary>
    /// The number of archetypes in the world: one per distinct component set its entities have had, plus the empty
    /// archetype, so at least 1. Archetypes are never destroyed while the world lives, so the count never decreases.
    /// </summary>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>Allocates nothing. Not thread-safe: like every member, it is for the owner thread only.</remarks>
    public int ArchetypeCount
    {
        get
        {
            ThrowIfUnusable("EcsWorld.ArchetypeCount");
            return _archetypes.Count;
        }
    }

    /// <summary>
    /// The number of chunks holding entities, over every archetype. Empty chunks kept in the pool for reuse aren't
    /// counted: an archetype returns a chunk to the pool as soon as it empties.
    /// </summary>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>
    /// Allocates nothing. Takes time proportional to <see cref="ArchetypeCount"/>: it sums the archetypes' chunk counts.
    /// Not thread-safe: like every member, it is for the owner thread only.
    /// </remarks>
    public int ChunkCount
    {
        get
        {
            ThrowIfUnusable("EcsWorld.ChunkCount");
            int chunks = 0;
            for (int i = 0; i < _archetypes.Count; i++)
            {
                chunks += _archetypes[i].Chunks.ChunkCount;
            }

            return chunks;
        }
    }

    /// <summary>
    /// Returns whether <paramref name="entity"/> is alive in this world: issued by it, placed in storage, and not
    /// despawned since. A lookup: it never throws for an entity that isn't alive here.
    /// </summary>
    /// <param name="entity">Any entity value.</param>
    /// <returns>
    /// <see langword="true"/> if the entity is alive. <see langword="false"/> for <see cref="Entity.Null"/>, a stale
    /// entity (despawned, or its slot retired), a pending entity (reserved by a command buffer and not yet placed), and
    /// an entity whose index is outside this world's table. An entity from another world that matches a live slot
    /// here returns <see langword="true"/>: entities carry no world reference.
    /// </returns>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>Allocates nothing.</remarks>
    public bool IsAlive(Entity entity)
    {
        ThrowIfUnusable("EcsWorld.IsAlive");
        return _entities.IsAlive(entity);
    }

    /// <summary>
    /// Returns whether <paramref name="entity"/> is pending in this world: reserved by a command buffer and not yet
    /// placed by its playback, nor released by clearing or disposing the buffer. A lookup: it never throws for an
    /// entity that isn't pending here.
    /// </summary>
    /// <param name="entity">Any entity value.</param>
    /// <returns>
    /// <see langword="true"/> if the entity is pending. <see langword="false"/> for <see cref="Entity.Null"/>, alive,
    /// stale and out-of-range entities.
    /// </returns>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>Allocates nothing.</remarks>
    public bool IsPending(Entity entity)
    {
        ThrowIfUnusable("EcsWorld.IsPending");
        return _entities.IsPending(entity);
    }

    /// <summary>
    /// Returns the world's pooled empty chunks to the allocator. Chunks in use stay where they are; storage that needs
    /// a chunk later allocates a new one.
    /// </summary>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>
    /// Emptied chunks go back to a pool rather than the allocator, so steady spawn and despawn churn makes no
    /// allocator calls; call this after a level unloads or a burst of entities is gone to give that memory back.
    /// Allocates no managed memory. Frees one block per pooled chunk.
    /// </remarks>
    public void TrimMemory()
    {
        ThrowIfUnusable("EcsWorld.TrimMemory");
        _chunkPool.Trim();
    }

    /// <summary>The allocator every unmanaged block of this world comes from. Not owned by the world.</summary>
    internal INativeAllocator Allocator => _allocator;

    /// <summary>The world's entity table, which the world owns and frees in <see cref="Dispose"/>.</summary>
    internal EntityTable Entities => _entities;

    /// <summary>The world's pool of chunk blocks, which the world owns and frees in <see cref="Dispose"/>.</summary>
    internal ChunkPool ChunkPool => _chunkPool;

    /// <summary>
    /// The world's archetypes, which the world owns and disposes in <see cref="Dispose"/> before the chunk pool.
    /// </summary>
    internal ArchetypeSet Archetypes => _archetypes;

    /// <summary>
    /// Frees everything the world allocated, the entity table, the archetypes' chunks and pooled chunks included.
    /// Idempotent: later calls do nothing.
    /// </summary>
    /// <remarks>
    /// Afterwards every other member throws <see cref="ObjectDisposedException"/>. The allocator from
    /// <see cref="WorldOptions.Allocator"/> is not disposed. Allocates nothing.
    /// </remarks>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    public void Dispose()
    {
        _owner.ThrowIfNotCurrentThread("EcsWorld.Dispose");
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _components.MarkDisposed();
        _entities.Dispose();

        // Before the pool: the archetypes return their chunks to it.
        try
        {
            _archetypes.Dispose();
        }
        finally
        {
            _chunkPool.Dispose();
        }
    }

    private void ThrowIfUnusable(string apiName)
    {
        _owner.ThrowIfNotCurrentThread(apiName);
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void Validate(WorldOptions options)
    {
        if (!BitOperations.IsPow2(options.ChunkBytes)
            || options.ChunkBytes is < WorldOptions.SmallestChunkBytes or > WorldOptions.LargestChunkBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.ChunkBytes,
                $"WorldOptions.ChunkBytes must be a power of two from {WorldOptions.SmallestChunkBytes} to {WorldOptions.LargestChunkBytes}.");
        }

        // MaxEntities first: the range of InitialEntityCapacity depends on it.
        if (options.MaxEntities < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxEntities, "WorldOptions.MaxEntities must be at least 1.");
        }

        if (options.InitialEntityCapacity < 1 || options.InitialEntityCapacity > options.MaxEntities)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.InitialEntityCapacity,
                $"WorldOptions.InitialEntityCapacity must be from 1 to WorldOptions.MaxEntities ({options.MaxEntities}).");
        }

        if (options.MaxComponentTypes is < WorldOptions.ComponentTypeGranularity or > WorldOptions.LargestMaxComponentTypes
            || options.MaxComponentTypes % WorldOptions.ComponentTypeGranularity != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxComponentTypes,
                $"WorldOptions.MaxComponentTypes must be a multiple of {WorldOptions.ComponentTypeGranularity} from {WorldOptions.ComponentTypeGranularity} to {WorldOptions.LargestMaxComponentTypes}.");
        }

        if (options.MaxHierarchyDepth < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxHierarchyDepth, "WorldOptions.MaxHierarchyDepth must be at least 1.");
        }

        if (options.Clock is null)
        {
            throw new ArgumentException("WorldOptions.Clock must not be null.", nameof(options));
        }

        if (string.IsNullOrEmpty(options.Name))
        {
            throw new ArgumentException("WorldOptions.Name must not be null or empty.", nameof(options));
        }
    }
}
