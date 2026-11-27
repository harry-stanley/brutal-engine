using System;
using System.Collections.Generic;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;

namespace BrutalEngine.Graphics.Internal;

/// <summary>
/// A graphics device's handle tables: one Core <see cref="HandleAllocator"/> per <see cref="BackendObjectKind"/>, the
/// handle-to-id entries of the device's <see cref="BackendObjectTable"/>, each live object's debug name and creation
/// order, each texture's description and default view, each view's texture, each buffer's description, mapped pointer
/// and last-written frame, and which handles the device owns itself: transient aliases and swapchain images
/// (docs/design/graphics.md sections 1, 3 and 6).
/// </summary>
/// <remarks>
/// <para>
/// A handle is live from <see cref="Add"/> until <see cref="Remove"/> or <see cref="TakeAll"/>; then its slot's generation
/// moves on, so every copy is stale. Validation never dereferences a handle: a null handle throws
/// <see cref="ArgumentException"/>, any handle the kind's allocator doesn't hold as live throws
/// <see cref="StaleHandleException"/>.
/// </para>
/// <para>
/// An alias (<see cref="AddAlias"/>) is a live buffer handle for a staging block the device owns, and a swapchain image
/// (<see cref="AddSwapchainImage"/>) a live texture and view pair its swapchain owns. Both are <i>owned</i> entries: they
/// resolve like any handle, but <see cref="LiveCount"/>, <see cref="ListLive"/> and <see cref="TakeAll"/> leave them out,
/// so they are never counted, reported as leaks or destroyed through the tables; only <see cref="Remove"/> (which
/// <see cref="RemoveAlias"/> calls), <see cref="TryRemoveViewOf"/> or <see cref="TakeAll"/> ends them.
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
    private BufferMemory[] _memory = [];
    private long _nextSequence;
    private int _ownedCount;

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

    /// <summary>The number of live objects over every kind, owned entries (aliases, swapchain images) left out.</summary>
    public int LiveCount
    {
        get
        {
            int count = -_ownedCount;
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

            if (kind == BackendObjectKind.Buffer && handle.Index >= (uint)_memory.Length)
            {
                Array.Resize(ref _memory, entries.Length);
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
        MarkOwned(BackendObjectKind.Buffer, alias);
        _buffers[alias.Index] = description;
        return alias;
    }

    /// <summary>Makes a live alias stale; its block is the caller's, and nothing is destroyed.</summary>
    /// <param name="alias">A live alias from <see cref="AddAlias"/>.</param>
    public void RemoveAlias(Handle alias) => _ = Remove(BackendObjectKind.Buffer, alias, nameof(alias));

    /// <summary>Whether a live buffer handle is an alias.</summary>
    /// <param name="buffer">A live buffer handle.</param>
    /// <returns><see langword="true"/> for a handle <see cref="AddAlias"/> issued.</returns>
    public bool IsAlias(Handle buffer) => IsOwned(BackendObjectKind.Buffer, buffer);

    /// <summary>
    /// Issues the texture and view handles of one swapchain image, owned by its swapchain: the view is the texture's
    /// default view, and the texture reports <paramref name="description"/>.
    /// </summary>
    /// <param name="textureId">The image's texture id, from the backend's swapchain-image query.</param>
    /// <param name="viewId">The image's view id, from the same query.</param>
    /// <param name="description">What the image reports as its texture's description.</param>
    /// <returns>The new live texture and view handles.</returns>
    /// <exception cref="OutOfMemoryException">A table couldn't grow; nothing was recorded.</exception>
    /// <exception cref="InvalidOperationException">A kind's handle allocator is exhausted; nothing was recorded.</exception>
    public (Handle Texture, Handle View) AddSwapchainImage(ulong textureId, ulong viewId, in TextureDescription description)
    {
        Handle texture = Add(BackendObjectKind.Texture, textureId, description.DebugName);
        MarkOwned(BackendObjectKind.Texture, texture);
        Handle view;
        try
        {
            view = Add(BackendObjectKind.TextureView, viewId, description.DebugName, texture);
        }
        catch
        {
            _ = Remove(BackendObjectKind.Texture, texture, nameof(textureId));
            throw;
        }

        MarkOwned(BackendObjectKind.TextureView, view);
        SetTexture(texture, description, view);
        return (texture, view);
    }

    /// <summary>Whether a live handle is owned by the device or a swapchain: an alias or a swapchain image.</summary>
    /// <param name="kind">The handle's kind.</param>
    /// <param name="handle">A live handle of that kind.</param>
    /// <returns><see langword="true"/> for a handle <see cref="AddAlias"/> or <see cref="AddSwapchainImage"/> issued.</returns>
    public bool IsOwned(BackendObjectKind kind, Handle handle) => _entries[(int)kind][handle.Index].Owned;

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

    /// <summary>Records a live buffer's description and mapped pointer; the buffer starts unwritten.</summary>
    /// <param name="buffer">A live buffer.</param>
    /// <param name="description">The description it was created from.</param>
    /// <param name="mapped">The backend's mapped pointer for it; 0 when it isn't host memory.</param>
    public void SetBuffer(Handle buffer, in BufferDescription description, nint mapped)
    {
        _buffers[buffer.Index] = description;
        _memory[buffer.Index] = new BufferMemory(mapped, LastWritten: -1);
    }

    /// <summary>Records that a frame writes a live buffer: a copy into it, or a write it lands.</summary>
    /// <param name="buffer">A live buffer from <see cref="Add"/>, not an alias.</param>
    /// <param name="frameNumber">The frame's number; never lower than an earlier call's.</param>
    /// <remarks>Allocates nothing.</remarks>
    public void MarkWritten(Handle buffer, long frameNumber)
    {
        ref BufferMemory memory = ref _memory[buffer.Index];
        memory = memory with { LastWritten = frameNumber };
    }

    /// <summary>Returns the mapped pointer and the last frame that writes a live buffer.</summary>
    /// <param name="buffer">A live buffer from <see cref="Add"/>, not an alias.</param>
    /// <returns>The pointer <see cref="SetBuffer"/> recorded, and the frame of the last <see cref="MarkWritten"/>; -1 before one.</returns>
    public BufferMemory GetMemory(Handle buffer) => _memory[buffer.Index];

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
                _ownedCount -= views[cursor].Owned ? 1 : 0;
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
        _ownedCount -= _entries[(int)kind][handle.Index].Owned ? 1 : 0;
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

    /// <summary>Lists every live object but the owned entries, oldest first, without changing anything.</summary>
    /// <returns>Each object's kind, backend id and debug name, in creation order. Allocates the list.</returns>
    public List<LiveObject> ListLive()
    {
        var live = new List<LiveObject>(LiveCount);
        for (int kind = 0; kind < KindCount; kind++)
        {
            foreach (Entry entry in _entries[kind])
            {
                if (entry.Id != 0 && !entry.Owned)
                {
                    live.Add(new LiveObject((BackendObjectKind)kind, entry.Id, entry.DebugName, entry.Sequence));
                }
            }
        }

        live.Sort(static (a, b) => a.Sequence.CompareTo(b.Sequence));
        return live;
    }

    /// <summary>Removes every live object, owned entries included, and returns them newest first.</summary>
    /// <returns>
    /// Each object's kind, backend id and debug name, in reverse creation order, owned entries left out (alias blocks are
    /// the device's to destroy, swapchain images their swapchain's). Allocates the list.
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
        _ownedCount = 0;
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

    /// <summary>Takes the next creation-order number, for an object the device tracks outside these tables (a swapchain).</summary>
    /// <returns>A number larger than every one taken before, by this call or by <see cref="Add"/>.</returns>
    public long NextSequence() => _nextSequence++;

    // The kind is formatted only on the throw path: an enum's ToString allocates.
    private static void ThrowIfNull(BackendObjectKind kind, Handle handle, string parameterName)
    {
        if (handle.IsNull)
        {
            throw new ArgumentException($"The {kind} handle is null.", parameterName);
        }
    }

    private void MarkOwned(BackendObjectKind kind, Handle handle)
    {
        ref Entry entry = ref _entries[(int)kind][handle.Index];
        entry = entry with { Owned = true };
        _ownedCount++;
    }

    /// <summary>A buffer's host memory and write tracking, set at creation; an alias's entry is never read.</summary>
    /// <param name="Mapped">The backend's mapped pointer; 0 when the buffer isn't host memory.</param>
    /// <param name="LastWritten">The number of the last frame that writes the buffer; -1 when none has.</param>
    internal readonly record struct BufferMemory(nint Mapped, long LastWritten);

    /// <summary>A live object as <see cref="TakeAll"/> returns it.</summary>
    /// <param name="Kind">The object's kind.</param>
    /// <param name="Id">The backend's id.</param>
    /// <param name="DebugName">The description's debug name, or <see langword="null"/>.</param>
    /// <param name="Sequence">The object's creation order on this device, from 0.</param>
    internal readonly record struct LiveObject(BackendObjectKind Kind, ulong Id, string? DebugName, long Sequence);

    // Id 0 marks a free slot: backends never issue it. Link is a texture's default view, or a view's texture. Owned marks
    // a transient alias of a staging block or a swapchain image.
    private readonly record struct Entry(Handle Handle, ulong Id, string? DebugName, long Sequence, Handle Link, bool Owned = false);
}
