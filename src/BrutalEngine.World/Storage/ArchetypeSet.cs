using System;
using System.Runtime.ExceptionServices;
using BrutalEngine.Core.Memory;

namespace BrutalEngine.World.Storage;

/// <summary>
/// A world's archetypes and its signature map: finds the archetype of a component set, creating it on first use
/// (docs/design/world.md, section 4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Indices.</b> The empty archetype (no components) is index <c>0</c>, created with the set. Every other archetype
/// gets the next index when it is first asked for, so indices follow creation order, and the same sequence of requests
/// gives the same indices in every run. Archetypes are never removed.
/// </para>
/// <para>
/// <b>Signature map.</b> An open-addressing table of archetype indices with linear probing, keyed by
/// <see cref="ArchetypeSignature.ComputeHash"/> (a fixed function, so probing never depends on addresses or a
/// per-process seed) and kept at most half full. It is only searched, never iterated for an order.
/// </para>
/// <para>
/// <b>Allocation.</b> Creating an archetype allocates its managed metadata (signature, layout, chunk table object) and
/// grows the set's managed tables when they are full, all before anything visible changes, so an archetype that can't
/// be created (a row too wide, an invalid id) leaves the set unchanged. Creating one allocates no native memory: its
/// chunk table allocates when it gets its first chunk. Finding an existing archetype allocates nothing, once the set's
/// id buffer holds the request: only a request longer than every earlier one grows it.
/// </para>
/// <para>Not thread-safe: the owning world's owner thread is its only caller.</para>
/// </remarks>
internal sealed class ArchetypeSet : IDisposable
{
    /// <summary>The length of a new set's archetype list.</summary>
    public const int InitialCapacity = 16;

    private readonly INativeAllocator _allocator;
    private readonly ChunkPool _pool;
    private readonly ComponentRegistry _registry;
    private readonly int _maskBits;
    private Archetype[] _archetypes = new Archetype[InitialCapacity];

    // Archetype index + 1 per slot, 0 for an empty slot. A power-of-two length, at most half full.
    private int[] _slots = new int[InitialCapacity * 2];

    // Holds a request's ids while it is looked up.
    private int[] _ids = new int[InitialCapacity];
    private int _count;
    private bool _disposed;

    /// <summary>Creates a set holding only the empty archetype, at index 0. Allocates no native memory.</summary>
    /// <param name="allocator">Source of the archetypes' native arrays: the world's allocator. Not owned; must outlive the set.</param>
    /// <param name="pool">Source of the archetypes' chunks: the world's pool. Not owned; must outlive the set.</param>
    /// <param name="registry">The world's component registry, which describes every id the set is given.</param>
    /// <param name="maskBits">The width of the archetypes' masks: the world's <c>MaxComponentTypes</c>, already validated.</param>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/>, <paramref name="pool"/> or <paramref name="registry"/> is <see langword="null"/>.</exception>
    public ArchetypeSet(INativeAllocator allocator, ChunkPool pool, ComponentRegistry registry, int maskBits)
    {
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(registry);
        _allocator = allocator;
        _pool = pool;
        _registry = registry;
        _maskBits = maskBits;
        _ = Create([], ArchetypeSignature.ComputeHash([]));
    }

    /// <summary>The number of archetypes, the empty one included: always at least 1, and <c>0</c> once disposed.</summary>
    public int Count => _count;

    /// <summary>The allocator the archetypes' native arrays come from. Not owned by the set.</summary>
    public INativeAllocator Allocator => _allocator;

    /// <summary>The empty archetype, index 0: the archetype of an entity with no components.</summary>
    /// <exception cref="ObjectDisposedException">The set is disposed.</exception>
    public Archetype Empty => this[0];

    /// <summary>
    /// Test seam: the number of times the signature map has been searched. Every <see cref="GetOrCreate"/> searches it
    /// once, so a test can prove that a cached path didn't. One increment per search; the engine never reads it.
    /// </summary>
    internal int SignatureLookups { get; private set; }

    /// <summary>Returns the archetype at <paramref name="index"/>.</summary>
    /// <param name="index">The archetype's index, from 0 to <see cref="Count"/> - 1.</param>
    /// <returns>The archetype.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> isn't an archetype's index.</exception>
    /// <exception cref="ObjectDisposedException">The set is disposed.</exception>
    public Archetype this[int index]
    {
        get
        {
            ThrowIfDisposed();
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
            return _archetypes[index];
        }
    }

    /// <summary>Returns the archetype of a component set, creating it if it doesn't exist yet.</summary>
    /// <param name="components">The components, in any order; repeats count once. Ids of this world's registry.</param>
    /// <returns>The archetype whose signature is exactly the distinct <paramref name="components"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An id's index is negative or at least the registry's <see cref="ComponentRegistry.Count"/>. The set is unchanged.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The archetype is new and its row is too wide for a chunk (see <see cref="ChunkLayout.Compute"/>); the message
    /// names the components. The set is unchanged.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The set is disposed.</exception>
    public Archetype GetOrCreate(ReadOnlySpan<ComponentId> components)
    {
        ThrowIfDisposed();
        int registered = _registry.Count;
        if (components.Length > _ids.Length)
        {
            // A request with repeats can be longer than every signature. A bigger buffer changes nothing visible.
            _ids = new int[Math.Max(components.Length, _ids.Length * 2)];
        }

        Span<int> ids = _ids.AsSpan(0, components.Length);
        for (int i = 0; i < components.Length; i++)
        {
            int id = components[i].Index;
            if ((uint)id >= (uint)registered)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(components), id, $"Component id {id} isn't registered: the registry has {registered} component types.");
            }

            ids[i] = id;
        }

        return GetOrCreateSorted(ids[..ArchetypeSignature.Normalize(ids)]);
    }

    /// <summary>
    /// Disposes every archetype, returning their chunks to the pool, which must not be disposed yet. Idempotent;
    /// afterwards the lookups throw <see cref="ObjectDisposedException"/>.
    /// </summary>
    /// <remarks>
    /// Every archetype is disposed even if one throws; the first exception is rethrown after the rest (see
    /// <see cref="ChunkTable.Dispose"/> for what a throwing table leaves behind).
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The pool was disposed before the set and an archetype held chunks.</exception>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        int count = _count;
        _count = 0;
        ExceptionDispatchInfo? first = null;
        for (int i = 0; i < count; i++)
        {
            try
            {
                _archetypes[i].Dispose();
            }
            catch (Exception ex)
            {
                // Keep going, so the later archetypes still return their chunks; the first failure is reported.
                first ??= ExceptionDispatchInfo.Capture(ex);
            }
        }

        first?.Throw();
    }

    private Archetype GetOrCreateSorted(ReadOnlySpan<int> sortedIds)
    {
        int hash = ArchetypeSignature.ComputeHash(sortedIds);
        int found = Find(sortedIds, hash);
        return found >= 0 ? _archetypes[found] : Create(sortedIds, hash);
    }

    private int Find(ReadOnlySpan<int> sortedIds, int hash)
    {
        SignatureLookups++;
        int mask = _slots.Length - 1;
        for (int slot = hash & mask; _slots[slot] != 0; slot = (slot + 1) & mask)
        {
            ArchetypeSignature signature = _archetypes[_slots[slot] - 1].Signature;
            if (signature.Hash == hash && signature.Matches(sortedIds))
            {
                return _slots[slot] - 1;
            }
        }

        // The map is at most half full, so the probe always reaches an empty slot.
        return -1;
    }

    private Archetype Create(ReadOnlySpan<int> sortedIds, int hash)
    {
        // Everything that can throw comes first: the layout (a row too wide), the signature, and growing the managed
        // tables. A longer table with nothing new in it changes nothing visible, so the set is unchanged on a throw.
        var types = new ComponentType[sortedIds.Length];
        for (int i = 0; i < types.Length; i++)
        {
            types[i] = _registry.GetComponentType(new ComponentId(sortedIds[i]));
        }

        var layout = ChunkLayout.Compute(types, _pool.ChunkBytes);
        var signature = ArchetypeSignature.Create(sortedIds, _maskBits);
        int index = _count;
        if (index == _archetypes.Length)
        {
            Array.Resize(ref _archetypes, checked(index * 2));
        }

        if ((index + 1) * 2 > _slots.Length)
        {
            _slots = Rehash(checked(_slots.Length * 2));
        }

        // Allocates no native memory, and can't throw: the column count is at most the mask width, 4,096.
        Archetype archetype = new(this, index, signature, layout, new ChunkTable(_allocator, _pool, layout.ColumnCount));
        _archetypes[index] = archetype;
        Insert(_slots, hash, index);
        _count = index + 1;
        return archetype;
    }

    private int[] Rehash(int length)
    {
        int[] slots = new int[length];
        for (int i = 0; i < _count; i++)
        {
            Insert(slots, _archetypes[i].Signature.Hash, i);
        }

        return slots;
    }

    private static void Insert(int[] slots, int hash, int index)
    {
        int mask = slots.Length - 1;
        int slot = hash & mask;
        while (slots[slot] != 0)
        {
            slot = (slot + 1) & mask;
        }

        slots[slot] = index + 1;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
