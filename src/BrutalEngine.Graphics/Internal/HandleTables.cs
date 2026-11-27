using System;
using System.Collections.Generic;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;

namespace BrutalEngine.Graphics.Internal;

/// <summary>
/// A graphics device's handle tables: one Core <see cref="HandleAllocator"/> per <see cref="BackendObjectKind"/>, the
/// handle-to-id entries of the device's <see cref="BackendObjectTable"/>, each live object's debug name and creation
/// order, each texture's description and default view, each view's texture, and which buffer handles are transient
/// aliases (docs/design/graphics.md sections 1 and 6).
/// </summary>
/// <remarks>
/// <para>
/// A handle is live from <see cref="Add"/> until <see cref="Remove"/> or <see cref="TakeAll"/>; then its slot's generation
/// moves on, so every copy is stale. Validation never dereferences a handle: a null handle throws
/// <see cref="ArgumentException"/>, any handle the kind's allocator doesn't hold as live throws
/// <see cref="StaleHandleException"/>.
/// </para>
/// <para>
/// An alias (<see cref="AddAlias"/>) is a live buffer handle for a staging block the device owns: it resolves like any
/// buffer, but <see cref="LiveCount"/>, <see cref="ListLive"/> and <see cref="TakeAll"/> leave it out, so it is never
/// counted, reported as a leak or destroyed through the tables, and only <see cref="RemoveAlias"/> or <see cref="TakeAll"/>
/// ends it.
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
    private TextureDescription[] _textures = [];
    private BufferDescription[] _buffers = [];
    private long _nextSequence;
    private int _aliasCount;

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

    /// <summary>The number of live objects over every kind, aliases left out.</summary>
    public int LiveCount
    {
        get
        {
            int count = -_aliasCount;
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
    /// <param name="link">For a texture view, the texture it views; otherwise the null handle.</param>
    /// <returns>The new live handle.</returns>
    /// <exception cref="OutOfMemoryException">A table couldn't grow; nothing was recorded.</exception>
    /// <exception cref="InvalidOperationException">The kind's handle allocator is exhausted; nothing was recorded.</exception>
    public Handle Add(BackendObjectKind kind, ulong id, string? debugName, Handle link = default)
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

            if (kind == BackendObjectKind.Texture && handle.Index >= (uint)_textures.Length)
            {
                Array.Resize(ref _textures, entries.Length);
            }

            if (kind == BackendObjectKind.Buffer && handle.Index >= (uint)_buffers.Length)
            {
                Array.Resize(ref _buffers, entries.Length);
            }

            _objects.Set(kind, handle, id);
            entries[handle.Index] = new Entry(handle, id, debugName, _nextSequence++, link);
            return handle;
        }
        catch
        {
            allocator.Free(handle);
            throw;
        }
    }

    /// <summary>Issues an alias: a live buffer handle for a staging block the device owns, resolving to its id.</summary>
    /// <param name="id">The block's backend id.</param>
    /// <param name="description">What the alias reports as its buffer's description.</param>
    /// <returns>The new live handle.</returns>
    /// <exception cref="OutOfMemoryException">A table couldn't grow; nothing was recorded.</exception>
    /// <exception cref="InvalidOperationException">The buffer handle allocator is exhausted; nothing was recorded.</exception>
    public Handle AddAlias(ulong id, in BufferDescription description)
    {
        Handle alias = Add(BackendObjectKind.Buffer, id, debugName: null);
        ref Entry entry = ref _entries[(int)BackendObjectKind.Buffer][alias.Index];
        entry = entry with { Alias = true };
        _buffers[alias.Index] = description;
        _aliasCount++;
        return alias;
    }

    /// <summary>Makes a live alias stale; its block is the caller's, and nothing is destroyed.</summary>
    /// <param name="alias">A live alias from <see cref="AddAlias"/>.</param>
    public void RemoveAlias(Handle alias)
    {
        _ = Remove(BackendObjectKind.Buffer, alias, nameof(alias));
        _aliasCount--;
    }

    /// <summary>Whether a live buffer handle is an alias.</summary>
    /// <param name="buffer">A live buffer handle.</param>
    /// <returns><see langword="true"/> for a handle <see cref="AddAlias"/> issued.</returns>
    public bool IsAlias(Handle buffer) => _entries[(int)BackendObjectKind.Buffer][buffer.Index].Alias;

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

    /// <summary>Records a live texture's description and default view.</summary>
    /// <param name="texture">A live texture.</param>
    /// <param name="description">The description it was created from.</param>
    /// <param name="defaultView">Its default view, live.</param>
    public void SetTexture(Handle texture, in TextureDescription description, Handle defaultView)
    {
        _textures[texture.Index] = description;
        ref Entry entry = ref _entries[(int)BackendObjectKind.Texture][texture.Index];
        entry = entry with { Link = defaultView };
    }

    /// <summary>Records a live buffer's description.</summary>
    /// <param name="buffer">A live buffer.</param>
    /// <param name="description">The description it was created from.</param>
    public void SetBuffer(Handle buffer, in BufferDescription description) => _buffers[buffer.Index] = description;

    /// <summary>Returns the description a live buffer was created from.</summary>
    /// <param name="buffer">A live buffer.</param>
    /// <returns>The description, by reference into the table; valid until the buffer is removed.</returns>
    public ref readonly BufferDescription GetBuffer(Handle buffer) => ref _buffers[buffer.Index];

    /// <summary>Returns the description a live texture was created from.</summary>
    /// <param name="texture">A live texture.</param>
    /// <returns>The description, by reference into the table; valid until the texture is removed.</returns>
    public ref readonly TextureDescription GetTexture(Handle texture) => ref _textures[texture.Index];

    /// <summary>Returns a live object's link: a texture's default view, or a view's texture.</summary>
    /// <param name="kind">The object's kind.</param>
    /// <param name="handle">A live handle of that kind.</param>
    /// <returns>The linked handle; null for other kinds.</returns>
    public Handle GetLink(BackendObjectKind kind, Handle handle) => _entries[(int)kind][handle.Index].Link;

    /// <summary>Removes the next live view of a texture, scanning the view table from a cursor.</summary>
    /// <param name="texture">The texture whose views are removed.</param>
    /// <param name="cursor">Where the scan resumes; start at 0 and pass the same variable back each time.</param>
    /// <param name="viewId">The removed view's backend id, for the caller to destroy.</param>
    /// <returns><see langword="true"/> when a view was removed; <see langword="false"/> once none is left.</returns>
    /// <remarks>Allocates nothing.</remarks>
    public bool TryRemoveViewOf(Handle texture, ref int cursor, out ulong viewId)
    {
        const BackendObjectKind View = BackendObjectKind.TextureView;
        Entry[] views = _entries[(int)View];
        for (; cursor < views.Length; cursor++)
        {
            if (views[cursor].Id != 0 && views[cursor].Link == texture)
            {
                viewId = views[cursor].Id;
                Handle view = views[cursor].Handle;
                _objects.Remove(View, view);
                _allocators[(int)View].Free(view);
                views[cursor] = default;
                return true;
            }
        }

        viewId = 0;
        return false;
    }

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
        if (kind == BackendObjectKind.Texture)
        {
            _textures[handle.Index] = default;
        }
        else if (kind == BackendObjectKind.Buffer)
        {
            _buffers[handle.Index] = default;
        }

        return id;
    }

    /// <summary>Lists every live object but the aliases, oldest first, without changing anything.</summary>
    /// <returns>Each object's kind, backend id and debug name, in creation order. Allocates the list.</returns>
    public List<LiveObject> ListLive()
    {
        var live = new List<LiveObject>(LiveCount);
        for (int kind = 0; kind < KindCount; kind++)
        {
            foreach (Entry entry in _entries[kind])
            {
                if (entry.Id != 0 && !entry.Alias)
                {
                    live.Add(new LiveObject((BackendObjectKind)kind, entry.Id, entry.DebugName, entry.Sequence));
                }
            }
        }

        live.Sort(static (a, b) => a.Sequence.CompareTo(b.Sequence));
        return live;
    }

    /// <summary>Removes every live object, aliases included, and returns them newest first.</summary>
    /// <returns>
    /// Each object's kind, backend id and debug name, in reverse creation order, aliases left out (their blocks are the
    /// device's to destroy). Allocates the list.
    /// </returns>
    public List<LiveObject> TakeAll()
    {
        List<LiveObject> live = ListLive();
        live.Reverse();
        for (int kind = 0; kind < KindCount; kind++)
        {
            _allocators[kind].Clear();
            Array.Clear(_entries[kind]);
        }

        Array.Clear(_textures);
        Array.Clear(_buffers);
        _aliasCount = 0;
        _objects.Clear();
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

    // Id 0 marks a free slot: backends never issue it. Link is a texture's default view, or a view's texture. Alias marks
    // a transient alias of a staging block.
    private readonly record struct Entry(Handle Handle, ulong Id, string? DebugName, long Sequence, Handle Link, bool Alias = false);
}
