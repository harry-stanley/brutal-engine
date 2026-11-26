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
/// id buffer holds the request: it always holds one more id than the largest archetype, so
/// <see cref="GetOrCreateAdding"/> and <see cref="GetOrCreateRemoving"/> never grow it, and only a
/// <see cref="GetOrCreate"/> request longer than every earlier one (it can repeat ids) grows it.
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

    // Holds a request's ids while it is looked up: at least one longer than the largest signature.
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
    /// Test seam: the number of times the signature map has been searched. Every <see cref="GetOrCreate"/>,
    /// <see cref="GetOrCreateAdding"/> and <see cref="GetOrCreateRemoving"/> searches it once, so a test can prove that a
    /// cached path didn't. One increment per search; the engine never reads it.
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

    /// <summary>Returns the archetype with <paramref name="source"/>'s components plus <paramref name="component"/>, creating it if needed.</summary>
    /// <param name="source">An archetype of this set that lacks <paramref name="component"/>.</param>
    /// <param name="component">The index of the component to add, an id of this world's registry.</param>
    /// <returns>The neighbouring archetype.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="source"/> belongs to another set.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="component"/> isn't registered.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="source"/> already has <paramref name="component"/> (internal misuse), or the neighbour is new and
    /// its row is too wide. The set is unchanged.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The set is disposed.</exception>
    public Archetype GetOrCreateAdding(Archetype source, int component)
    {
        ReadOnlySpan<int> ids = CheckNeighbour(source, component, adding: true);

        // Sound for the length: the buffer is at least one longer than every signature (see the class remarks).
        Span<int> neighbour = _ids.AsSpan(0, ids.Length + 1);
        int at = ~ids.BinarySearch(component);
        ids[..at].CopyTo(neighbour);
        neighbour[at] = component;
        ids[at..].CopyTo(neighbour[(at + 1)..]);
        return GetOrCreateSorted(neighbour);
    }

    /// <summary>Returns the archetype with <paramref name="source"/>'s components minus <paramref name="component"/>, creating it if needed.</summary>
    /// <param name="source">An archetype of this set that has <paramref name="component"/>.</param>
    /// <param name="component">The index of the component to remove, an id of this world's registry.</param>
    /// <returns>The neighbouring archetype.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="source"/> belongs to another set.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="component"/> isn't registered.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="source"/> lacks <paramref name="component"/> (internal misuse). The set is unchanged.</exception>
    /// <exception cref="ObjectDisposedException">The set is disposed.</exception>
    public Archetype GetOrCreateRemoving(Archetype source, int component)
    {
        ReadOnlySpan<int> ids = CheckNeighbour(source, component, adding: false);
        Span<int> neighbour = _ids.AsSpan(0, ids.Length - 1);
        int at = ids.BinarySearch(component);
        ids[..at].CopyTo(neighbour);
        ids[(at + 1)..].CopyTo(neighbour[at..]);
        return GetOrCreateSorted(neighbour);
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

    private ReadOnlySpan<int> CheckNeighbour(Archetype source, int component, bool adding)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        if (!ReferenceEquals(source.Set, this))
        {
            throw new ArgumentException("The archetype belongs to another world's archetype set.", nameof(source));
        }

        int registered = _registry.Count;
        if ((uint)component >= (uint)registered)
        {
            throw new ArgumentOutOfRangeException(
                nameof(component), component, $"Component id {component} isn't registered: the registry has {registered} component types.");
        }

        if (source.Signature.Contains(component) == adding)
        {
            throw new InvalidOperationException(adding
                ? $"Archetype {source.Index} already has component {component}; it has no add edge for it."
                : $"Archetype {source.Index} doesn't have component {component}; it has no remove edge for it.");
        }

        return source.Signature.Ids;
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
        int slot = hash & mask;

        // The map is at most half full, so the probe always reaches an empty slot; the bound only makes a broken map
        // fail a lookup instead of spinning.
        for (int probes = 0; probes < _slots.Length; probes++, slot = (slot + 1) & mask)
        {
            int entry = _slots[slot];
            if (entry == 0)
            {
                return -1;
            }

            ArchetypeSignature signature = _archetypes[entry - 1].Signature;
            if (signature.Hash == hash && signature.Matches(sortedIds))
            {
                return entry - 1;
            }
        }

        throw MapFull();
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

        if (signature.Count + 1 > _ids.Length)
        {
            _ids = new int[Math.Max(signature.Count + 1, _ids.Length * 2)];
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

        // The map is at most half full, so the probe always reaches an empty slot; the bound only makes a broken map
        // fail an insert instead of spinning.
        for (int probes = 0; probes < slots.Length; probes++, slot = (slot + 1) & mask)
        {
            if (slots[slot] == 0)
            {
                slots[slot] = index + 1;
                return;
            }
        }

        throw MapFull();
    }

    // An internal invariant is broken: the map should never be more than half full.
    private static InvalidOperationException MapFull() =>
        new("The archetype signature map has no empty slot; it should never be more than half full.");

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
