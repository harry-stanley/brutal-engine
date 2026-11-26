using System;
using System.Collections.Generic;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;

namespace BrutalEngine.Graphics.Internal;

/// <summary>
/// A graphics device's handle tables: one Core <see cref="HandleAllocator"/> per <see cref="BackendObjectKind"/>, the
/// handle-to-id entries of the device's <see cref="BackendObjectTable"/>, and each live object's debug name and creation
/// order (docs/design/graphics.md section 1).
/// </summary>
/// <remarks>
/// <para>
/// A handle is live from <see cref="Add"/> until <see cref="Remove"/> or <see cref="TakeAll"/>; then its slot's generation
/// moves on, so every copy is stale. Validation never dereferences a handle: a null handle throws
/// <see cref="ArgumentException"/>, any handle the kind's allocator doesn't hold as live throws
/// <see cref="StaleHandleException"/>.
/// </para>
/// <para>
/// Not thread-safe: the device calls it on its owner thread only. <see cref="Remove"/>, <see cref="GetId"/> and
/// <see cref="LiveCount"/> allocate nothing, except an exception they throw; <see cref="Add"/> allocates only when a
/// table grows.
/// </para>
/// </remarks>
internal sealed class HandleTables
{
    private const int KindCount = BackendObjectTable.KindCount;

    private readonly BackendObjectTable _objects;
    private readonly HandleAllocator[] _allocators = new HandleAllocator[KindCount];
    private readonly Entry[][] _entries = new Entry[KindCount][];
    private long _nextSequence;

    /// <summary>Creates empty tables that record ids in <paramref name="objects"/>, the table the backend reads.</summary>
    /// <param name="objects">The device's backend object table, empty.</param>
    public HandleTables(BackendObjectTable objects)
    {
        _objects = objects;
        for (int kind = 0; kind < KindCount; kind++)
        {
            _allocators[kind] = new HandleAllocator();
            _entries[kind] = [];
        }
    }

    /// <summary>The number of live objects over every kind.</summary>
    public int LiveCount
    {
        get
        {
            int count = 0;
            foreach (HandleAllocator allocator in _allocators)
            {
                count += allocator.Count;
            }

            return count;
        }
    }

    /// <summary>Issues a handle for an object the backend just created and records its id and debug name.</summary>
    /// <param name="kind">The object's kind.</param>
    /// <param name="id">The backend's id for it; non-zero.</param>
    /// <param name="debugName">The description's debug name, kept for diagnostics.</param>
    /// <returns>The new live handle.</returns>
    /// <exception cref="OutOfMemoryException">A table couldn't grow; nothing was recorded.</exception>
    /// <exception cref="InvalidOperationException">The kind's handle allocator is exhausted; nothing was recorded.</exception>
    public Handle Add(BackendObjectKind kind, ulong id, string? debugName)
    {
        HandleAllocator allocator = _allocators[(int)kind];
        Handle handle = allocator.Allocate();
        try
        {
            ref Entry[] entries = ref _entries[(int)kind];
            if (handle.Index >= (uint)entries.Length)
            {
                Array.Resize(ref entries, Math.Max(entries.Length * 2, (int)handle.Index + 1));
            }

            _objects.Set(kind, handle, id);
            entries[handle.Index] = new Entry(id, debugName, _nextSequence++);
            return handle;
        }
        catch
        {
            allocator.Free(handle);
            throw;
        }
    }

    /// <summary>Returns the backend id of a live object.</summary>
    /// <param name="kind">The object's kind.</param>
    /// <param name="handle">The handle to look up.</param>
    /// <param name="parameterName">The caller's parameter, named by the exceptions.</param>
    /// <returns>The object's backend id.</returns>
    /// <exception cref="ArgumentException"><paramref name="handle"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException"><paramref name="handle"/> isn't a live object of that kind.</exception>
    public ulong GetId(BackendObjectKind kind, Handle handle, string parameterName)
    {
        ThrowIfNotLive(kind, handle, parameterName);
        return _entries[(int)kind][handle.Index].Id;
    }

    /// <summary>Returns the debug name a live object was created with.</summary>
    /// <param name="kind">The object's kind.</param>
    /// <param name="handle">A live handle of that kind.</param>
    /// <returns>The description's debug name, or <see langword="null"/>.</returns>
    public string? GetDebugName(BackendObjectKind kind, Handle handle) => _entries[(int)kind][handle.Index].DebugName;

    /// <summary>Makes a live handle stale and removes its entries.</summary>
    /// <param name="kind">The object's kind.</param>
    /// <param name="handle">The handle to remove.</param>
    /// <param name="parameterName">The caller's parameter, named by the exceptions.</param>
    /// <returns>The object's backend id, for the caller to destroy.</returns>
    /// <exception cref="ArgumentException"><paramref name="handle"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException"><paramref name="handle"/> isn't a live object of that kind; nothing changed.</exception>
    public ulong Remove(BackendObjectKind kind, Handle handle, string parameterName)
    {
        ulong id = GetId(kind, handle, parameterName);
        _objects.Remove(kind, handle);
        _allocators[(int)kind].Free(handle);
        _entries[(int)kind][handle.Index] = default;
        return id;
    }

    /// <summary>Removes every live object, newest first, and returns them in that order.</summary>
    /// <returns>Each object's kind, backend id and debug name, in reverse creation order. Allocates the list.</returns>
    public List<LiveObject> TakeAll()
    {
        var live = new List<LiveObject>(LiveCount);
        for (int kind = 0; kind < KindCount; kind++)
        {
            Entry[] entries = _entries[kind];
            for (int index = 0; index < entries.Length; index++)
            {
                if (entries[index].Id != 0)
                {
                    live.Add(new LiveObject((BackendObjectKind)kind, entries[index].Id, entries[index].DebugName, entries[index].Sequence));
                }
            }

            _allocators[kind].Clear();
            Array.Clear(entries);
        }

        _objects.Clear();
        live.Sort(static (a, b) => b.Sequence.CompareTo(a.Sequence));
        return live;
    }

    /// <summary>Throws unless a handle is a live object of a kind.</summary>
    /// <param name="kind">The kind the handle must be.</param>
    /// <param name="handle">The handle to check.</param>
    /// <param name="parameterName">The caller's parameter, named by the exceptions.</param>
    /// <exception cref="ArgumentException"><paramref name="handle"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException"><paramref name="handle"/> isn't a live object of that kind.</exception>
    public void ThrowIfNotLive(BackendObjectKind kind, Handle handle, string parameterName)
    {
        ThrowIfNull(kind, handle, parameterName);
        if (!_allocators[(int)kind].IsValid(handle))
        {
            throw new StaleHandleException(handle);
        }
    }

    /// <summary>Throws for any swapchain handle: none can be created yet, so a non-null one is always stale.</summary>
    /// <param name="handle">The handle to check.</param>
    /// <param name="parameterName">The caller's parameter, named by the exceptions.</param>
    /// <exception cref="ArgumentException"><paramref name="handle"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">Always, for a non-null handle.</exception>
    public static void ThrowSwapchainNotLive(Handle handle, string parameterName)
    {
        ThrowIfNull(null, handle, parameterName);
        throw new StaleHandleException(handle);
    }

    // The kind is formatted only on the throw path: an enum's ToString allocates. Null stands for a swapchain.
    private static void ThrowIfNull(BackendObjectKind? kind, Handle handle, string parameterName)
    {
        if (handle.IsNull)
        {
            throw new ArgumentException($"The {(kind is { } k ? k.ToString() : "Swapchain")} handle is null.", parameterName);
        }
    }

    /// <summary>A live object as <see cref="TakeAll"/> returns it.</summary>
    /// <param name="Kind">The object's kind.</param>
    /// <param name="Id">The backend's id.</param>
    /// <param name="DebugName">The description's debug name, or <see langword="null"/>.</param>
    /// <param name="Sequence">The object's creation order on this device, from 0.</param>
    internal readonly record struct LiveObject(BackendObjectKind Kind, ulong Id, string? DebugName, long Sequence);

    // Id 0 marks a free slot: backends never issue it.
    private readonly record struct Entry(ulong Id, string? DebugName, long Sequence);
}
