using System;
using BrutalEngine.Core.Memory;

namespace BrutalEngine.World.Storage;

/// <summary>
/// An archetype's edges: for each component id it has been asked about, the index of the neighbouring archetype that
/// adds or removes that component (docs/design/world.md, section 4, "Edges").
/// </summary>
/// <remarks>
/// <para>
/// <b>One table for both directions.</b> An archetype either has a component or doesn't, so a component id names its
/// add edge (the archetype lacks it) or its remove edge (the archetype has it), never both. The table maps the id to
/// the neighbour's index; archetypes are never destroyed, so a cached edge never goes stale.
/// </para>
/// <para>
/// <b>Layout.</b> An open-addressing table of 8-byte entries (component id, archetype index) with linear probing, a
/// power-of-two capacity and a fixed multiplicative hash, so probe order never depends on addresses or a per-process
/// seed. An empty entry has component id <c>-1</c>. The table grows by doubling once it would be more than three
/// quarters full, so a probe always ends at an empty entry.
/// </para>
/// <para>
/// <b>Memory.</b> The entries come from the world's allocator, which the table doesn't own. The table allocates
/// nothing until its first <see cref="Add"/>: an archetype that is never left through an edge costs no native memory.
/// Growth allocates the new entries before it touches the old ones, so a failed allocation leaves the table
/// unchanged. No member allocates managed memory, except to throw.
/// </para>
/// <para>Not thread-safe: the owning world's owner thread is its only caller.</para>
/// </remarks>
internal sealed unsafe class ArchetypeEdges : IDisposable
{
    /// <summary>The capacity after the first <see cref="Add"/>, in entries.</summary>
    public const int InitialCapacity = 8;

    private const int EmptyKey = -1;

    // Entries are 8 bytes; 8-byte alignment keeps each in one cache line.
    private const nuint EntryAlignment = 8;

    private readonly INativeAllocator _allocator;
    private Entry* _entries;
    private int _capacity;
    private int _count;
    private bool _disposed;

    /// <summary>Creates an empty table. Allocates no native memory.</summary>
    /// <param name="allocator">Source of the entries: the world's allocator. Not owned; must outlive the table.</param>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> is <see langword="null"/>.</exception>
    public ArchetypeEdges(INativeAllocator allocator)
    {
        ArgumentNullException.ThrowIfNull(allocator);
        _allocator = allocator;
    }

    /// <summary>The number of cached edges.</summary>
    public int Count => _count;

    /// <summary>The number of entries the table holds before it grows. <c>0</c> before the first edge and once disposed.</summary>
    public int Capacity => _capacity;

    /// <summary>Looks up the cached neighbour for <paramref name="component"/>.</summary>
    /// <param name="component">The component id's index. Not negative.</param>
    /// <param name="archetype">The neighbour's index when the edge is cached; otherwise <c>-1</c>.</param>
    /// <returns><see langword="true"/> if the edge is cached.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="component"/> is negative.</exception>
    /// <exception cref="ObjectDisposedException">The table is disposed.</exception>
    public bool TryGet(int component, out int archetype)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(component);
        if (_capacity != 0)
        {
            int mask = _capacity - 1;
            int slot = Home(component, mask);

            // The load limit keeps an entry empty, so the probe ends there; the bound only makes a broken table fail
            // a lookup instead of spinning.
            for (int probes = 0; probes < _capacity; probes++, slot = (slot + 1) & mask)
            {
                // Sound: slot is masked to the capacity, and the entry array holds _capacity entries.
                Entry entry = _entries[slot];
                if (entry.Component == component)
                {
                    archetype = entry.Archetype;
                    return true;
                }

                if (entry.Component == EmptyKey)
                {
                    break;
                }
            }
        }

        archetype = -1;
        return false;
    }

    /// <summary>Caches the edge from <paramref name="component"/> to <paramref name="archetype"/>.</summary>
    /// <param name="component">The component id's index. Not negative, and not cached yet.</param>
    /// <param name="archetype">The neighbour's index. Not negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="component"/> or <paramref name="archetype"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">The edge for <paramref name="component"/> is already cached. The table is unchanged.</exception>
    /// <exception cref="ObjectDisposedException">The table is disposed.</exception>
    /// <exception cref="OutOfMemoryException">The allocator can't provide the grown entries. The table is unchanged.</exception>
    public void Add(int component, int archetype)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(component);
        ArgumentOutOfRangeException.ThrowIfNegative(archetype);
        if (TryGet(component, out int cached))
        {
            throw new InvalidOperationException(
                $"The edge for component {component} is already cached, to archetype {cached}.");
        }

        // Grow while the new entry would make the table more than three quarters full.
        if ((long)(_count + 1) * 4 > (long)_capacity * 3)
        {
            Grow();
        }

        Insert(_entries, _capacity - 1, component, archetype);
        _count++;
    }

    /// <summary>Frees the entries. Idempotent; afterwards other members throw <see cref="ObjectDisposedException"/>.</summary>
    /// <remarks>If the allocator's free throws, the exception propagates and the table is still disposed; the entries leak.</remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Detach before the free, so a throwing free leaves no freed array reachable from the table.
        _disposed = true;
        Entry* entries = _entries;
        _entries = null;
        _capacity = 0;
        _count = 0;
        if (entries != null)
        {
            _allocator.Free(entries);
        }
    }

    // Fibonacci hashing: a fixed odd multiplier spreads consecutive ids, and the high bits pick the home slot.
    private static int Home(int component, int mask) => (int)(((uint)component * 0x9E3779B9u) >> 16) & mask;

    private static void Insert(Entry* entries, int mask, int component, int archetype)
    {
        int slot = Home(component, mask);

        // Sound: slot is masked to the array's capacity, and the load limit keeps an empty entry, so the probe ends.
        while (entries[slot].Component != EmptyKey)
        {
            slot = (slot + 1) & mask;
        }

        entries[slot] = new Entry(component, archetype);
    }

    private void Grow()
    {
        int capacity = _capacity == 0 ? InitialCapacity : checked(_capacity * 2);
        var entries = (Entry*)_allocator.Allocate(checked((nuint)(uint)capacity * (nuint)sizeof(Entry)), EntryAlignment);

        // Sound: the new array holds capacity entries; every one is written empty before any is read.
        new Span<Entry>(entries, capacity).Fill(new Entry(EmptyKey, -1));
        for (int i = 0; i < _capacity; i++)
        {
            // Sound: i < _capacity, the old array's length.
            Entry entry = _entries[i];
            if (entry.Component != EmptyKey)
            {
                Insert(entries, capacity - 1, entry.Component, entry.Archetype);
            }
        }

        // Swap first: if the free of the old array throws, the table is already consistent on the new one.
        Entry* old = _entries;
        _entries = entries;
        _capacity = capacity;
        if (old != null)
        {
            _allocator.Free(old);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>One edge: a component id and the neighbouring archetype's index, or an empty entry.</summary>
    private readonly struct Entry(int component, int archetype)
    {
        /// <summary>The component id's index, or -1 for an empty entry.</summary>
        public readonly int Component = component;

        /// <summary>The neighbouring archetype's index.</summary>
        public readonly int Archetype = archetype;
    }
}
