using System;
using BrutalEngine.Core;

namespace BrutalEngine.Graphics.Vulkan.Resources;

/// <summary>
/// The backend's table for one <c>BackendObjectKind</c> (docs/design/graphics-vulkan.md section 6, "The resource
/// tracker and leak report"): maps the ids the backend hands to the front end to each object's entry (its native handles,
/// its VMA allocation and its debug name).
/// </summary>
/// <typeparam name="TEntry">What the table keeps per object.</typeparam>
/// <remarks>
/// <para>
/// An id is the raw value of a Core <see cref="Handle"/> from the table's own <see cref="HandleAllocator"/>, so it is
/// never zero, and once its entry is removed the id is stale: lookups and removals with it fail, even after the slot is
/// reused. A zero, stale or never-issued id is a <see langword="false"/> result, never an exception, so the frame path
/// can look ids up without a try block.
/// </para>
/// <para>
/// Enumeration runs newest first, the order shutdown destroys objects in (section 5). Removing an entry clears its slot,
/// so the table holds no reference to it afterwards.
/// </para>
/// <para>
/// Memory: one array per field, all of the same length, grown by doubling (from 4) only when no slot is free; growth
/// allocates every new array before it replaces any. Only <see cref="Add"/> grows the table: it allocates exactly when
/// it grows, and <see cref="TryGet"/>, <see cref="TryRemove"/> and enumeration allocate nothing.
/// </para>
/// <para>Not thread-safe: the device's owner thread uses it.</para>
/// </remarks>
internal sealed class ObjectTable<TEntry>
{
    private const int None = -1;

    private readonly HandleAllocator _ids;
    private TEntry[] _entries = [];
    private ulong[] _handles = [];
    private int[] _older = [];
    private int[] _newer = [];
    private int _newest = None;
    private int _slotsUsed;
    private int _version;

    /// <summary>Creates an empty table that can issue up to <see cref="HandleAllocator.MaxSlotLimit"/> ids.</summary>
    public ObjectTable()
        : this(HandleAllocator.MaxSlotLimit)
    {
    }

    /// <summary>Creates an empty table that can issue up to <paramref name="maxSlots"/> ids, so tests can exhaust it.</summary>
    /// <param name="maxSlots">The most slots the table uses, 1 to <see cref="HandleAllocator.MaxSlotLimit"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxSlots"/> is out of range.</exception>
    internal ObjectTable(int maxSlots) => _ids = new HandleAllocator(0, maxSlots);

    /// <summary>Gets the number of entries.</summary>
    public int Count => _ids.Count;

    /// <summary>Gets the number of slots the arrays hold, for the growth tests.</summary>
    internal int Capacity => _entries.Length;

    /// <summary>Adds an entry.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>Its id: never zero, and never one an earlier entry had.</returns>
    /// <exception cref="InvalidOperationException">Every slot is live or retired (<see cref="HandleAllocator.Allocate"/>).</exception>
    /// <remarks>Allocates only when no slot is free, to grow.</remarks>
    public ulong Add(TEntry entry)
    {
        // The allocator reuses a free slot before it opens a new one, so the arrays grow only when no slot is free.
        bool noFreeSlot = _slotsUsed - _ids.Count - _ids.RetiredCount == 0;
        if (noFreeSlot && _slotsUsed == _entries.Length && _entries.Length < _ids.MaxSlots)
        {
            Grow();
        }

        Handle handle = _ids.Allocate();
        int index = (int)handle.Index;
        _slotsUsed = Math.Max(_slotsUsed, index + 1);
        _entries[index] = entry;
        _handles[index] = handle.RawValue;
        _older[index] = _newest;
        _newer[index] = None;
        if (_newest != None)
        {
            _newer[_newest] = index;
        }

        _newest = index;
        _version++;
        return handle.RawValue;
    }

    /// <summary>Looks an entry up.</summary>
    /// <param name="id">An id; zero, stale or never issued gives <see langword="false"/>.</param>
    /// <param name="entry">The entry, or <see langword="default"/>.</param>
    /// <returns>Whether <paramref name="id"/> is live.</returns>
    /// <remarks>Allocates nothing.</remarks>
    public bool TryGet(ulong id, out TEntry entry)
    {
        if (!TryFind(id, out int index))
        {
            entry = default!;
            return false;
        }

        entry = _entries[index];
        return true;
    }

    /// <summary>Removes an entry; its id is stale from then on.</summary>
    /// <param name="id">An id; zero, stale or never issued gives <see langword="false"/> and changes nothing.</param>
    /// <param name="entry">The removed entry, or <see langword="default"/>.</param>
    /// <returns>Whether <paramref name="id"/> was live.</returns>
    /// <remarks>Clears the slot, so the table keeps no reference to the entry. Allocates nothing.</remarks>
    public bool TryRemove(ulong id, out TEntry entry)
    {
        if (!TryFind(id, out int index))
        {
            entry = default!;
            return false;
        }

        entry = _entries[index];
        int older = _older[index];
        int newer = _newer[index];
        if (older != None)
        {
            _newer[older] = newer;
        }

        if (newer != None)
        {
            _older[newer] = older;
        }
        else
        {
            _newest = older;
        }

        _entries[index] = default!;
        _ids.Free(Handle.FromRawValue(id));
        _version++;
        return true;
    }

    /// <summary>Gets an enumerator over the entries, newest first.</summary>
    /// <returns>The enumerator; adding or removing an entry while it runs makes its next step throw.</returns>
    /// <remarks>Allocates nothing.</remarks>
    public Enumerator GetEnumerator() => new(this);

    private bool TryFind(ulong id, out int index)
    {
        // A non-zero id with generation 0 is no handle's raw value: rejected here, as FromRawValue would throw for it.
        if ((id >> 32) == 0 || !_ids.IsValid(Handle.FromRawValue(id)))
        {
            index = None;
            return false;
        }

        index = (int)(uint)id;
        return true;
    }

    private void Grow()
    {
        int length = (int)Math.Min(Math.Max(4L, (long)_entries.Length * 2), _ids.MaxSlots);

        // Every array is allocated before any is replaced, and the allocator grows last, to the same length.
        var entries = new TEntry[length];
        ulong[] handles = new ulong[length];
        int[] older = new int[length];
        int[] newer = new int[length];
        _entries.CopyTo(entries, 0);
        _handles.CopyTo(handles, 0);
        _older.CopyTo(older, 0);
        _newer.CopyTo(newer, 0);
        _ids.EnsureCapacity(length);
        (_entries, _handles, _older, _newer) = (entries, handles, older, newer);
    }

    /// <summary>Walks a table's entries, newest first, as <c>(Id, Entry)</c> pairs.</summary>
    /// <remarks>A value type that allocates nothing; use it once, through <see langword="foreach"/>.</remarks>
    internal struct Enumerator
    {
        private readonly ObjectTable<TEntry> _table;
        private readonly int _version;
        private int _next;
        private int _current;

        /// <summary>Starts before the newest entry.</summary>
        /// <param name="table">The table.</param>
        internal Enumerator(ObjectTable<TEntry> table)
        {
            _table = table;
            _version = table._version;
            _next = table._newest;
            _current = None;
        }

        /// <summary>Gets the current entry and its id.</summary>
        public readonly (ulong Id, TEntry Entry) Current => (_table._handles[_current], _table._entries[_current]);

        /// <summary>Steps to the next older entry.</summary>
        /// <returns>Whether there is one.</returns>
        /// <exception cref="InvalidOperationException">The table changed since the enumerator was made.</exception>
        public bool MoveNext()
        {
            if (_version != _table._version)
            {
                throw new InvalidOperationException("The table changed during enumeration.");
            }

            if (_next == None)
            {
                return false;
            }

            _current = _next;
            _next = _table._older[_current];
            return true;
        }
    }
}
