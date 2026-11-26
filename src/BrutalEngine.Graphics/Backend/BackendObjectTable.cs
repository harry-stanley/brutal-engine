using System;
using System.Diagnostics;
using System.Text;
using BrutalEngine.Core;

namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// Maps the front end's handles to backend object ids, one table per <see cref="BackendObjectKind"/>, and holds the UTF-8
/// names of interned debug labels (docs/design/graphics.md, "The backend interface").
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership.</b> The front end creates one per graphics device, passes it to the backend's device creation
/// (<c>IGraphicsBackend.CreateDevice</c>), and is the only writer: it records each id when the backend creates the
/// object, removes it when the object is destroyed, and empties the object tables when the device is lost. A backend only reads it, to resolve the handles inside
/// descriptions and the labels the command stream names. A new table is empty, and a table can be shared by successive
/// backend devices of one graphics device (device recovery).
/// </para>
/// <para>
/// <b>Handles.</b> Every lookup is generation-checked: a null handle throws <see cref="ArgumentException"/>, and a handle with
/// no entry of that kind (never recorded, removed, or a previous generation of a reused slot) throws
/// <see cref="StaleHandleException"/>. Both are front-end bugs, because the front end validates every handle before it reaches
/// a backend. A handle from another device that has the same index and generation as a live entry here can't be told apart
/// from it.
/// </para>
/// <para>
/// <b>Threading.</b> Not thread-safe. Every member is called on the graphics device's owner thread, as every backend call is.
/// </para>
/// <para>
/// <b>Allocation.</b> <see cref="GetId(BufferHandle)"/> and its overloads and <see cref="GetLabel"/> allocate nothing, except
/// the exception when they throw. Recording an entry allocates only when its table grows, or for a label's name.
/// </para>
/// </remarks>
public sealed class BackendObjectTable
{
    /// <summary>The number of <see cref="BackendObjectKind"/> members, one object table each.</summary>
    internal const int KindCount = 10;

    /// <summary>The length a table first grows to, when the slot index allows.</summary>
    internal const int InitialCapacity = 16;

    private readonly Slots<ulong>[] _objects;
    private readonly Slots<byte[]> _labels = new();

    /// <summary>Creates an empty table. Only the front end records entries in it.</summary>
    /// <remarks>Allocates the table's empty storage.</remarks>
    public BackendObjectTable()
    {
        _objects = new Slots<ulong>[KindCount];
        for (int i = 0; i < KindCount; i++)
        {
            _objects[i] = new Slots<ulong>();
        }
    }

    /// <summary>
    /// Test seam: when set, called just before each array a table allocates to grow, with the table's kind (or
    /// <see langword="null"/> for the label table), so a test can throw there to stand in for running out of memory. The
    /// engine never sets it. Lookups never read it.
    /// </summary>
    internal Action<BackendObjectKind?>? BeforeGrowth { get; set; }

    /// <summary>Resolves a buffer handle to the id its backend returned.</summary>
    /// <param name="handle">A live buffer handle of the device that owns this table.</param>
    /// <returns>The buffer's backend id; never 0.</returns>
    /// <exception cref="ArgumentException"><paramref name="handle"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="handle"/> has no buffer entry: never recorded, removed, or from a previous generation of a
    /// reused slot.
    /// </exception>
    public ulong GetId(BufferHandle handle) => Find(BackendObjectKind.Buffer, handle.Value);

    /// <summary>Resolves a texture handle to the id its backend returned.</summary>
    /// <param name="handle">A live texture handle of the device that owns this table.</param>
    /// <returns>The texture's backend id; never 0.</returns>
    /// <exception cref="ArgumentException"><paramref name="handle"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="handle"/> has no texture entry: never recorded, removed, or from a previous generation of a
    /// reused slot.
    /// </exception>
    public ulong GetId(TextureHandle handle) => Find(BackendObjectKind.Texture, handle.Value);

    /// <summary>Resolves a texture view handle to the id its backend returned.</summary>
    /// <param name="handle">A live texture view handle of the device that owns this table.</param>
    /// <returns>The view's backend id; never 0.</returns>
    /// <exception cref="ArgumentException"><paramref name="handle"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="handle"/> has no texture view entry: never recorded, removed, or from a previous generation of a
    /// reused slot.
    /// </exception>
    public ulong GetId(TextureViewHandle handle) => Find(BackendObjectKind.TextureView, handle.Value);

    /// <summary>Resolves a sampler handle to the id its backend returned.</summary>
    /// <param name="handle">A live sampler handle of the device that owns this table.</param>
    /// <returns>The sampler's backend id; never 0.</returns>
    /// <exception cref="ArgumentException"><paramref name="handle"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="handle"/> has no sampler entry: never recorded, removed, or from a previous generation of a
    /// reused slot.
    /// </exception>
    public ulong GetId(SamplerHandle handle) => Find(BackendObjectKind.Sampler, handle.Value);

    /// <summary>Resolves a shader module handle to the id its backend returned.</summary>
    /// <param name="handle">A live shader module handle of the device that owns this table.</param>
    /// <returns>The shader module's backend id; never 0.</returns>
    /// <exception cref="ArgumentException"><paramref name="handle"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="handle"/> has no shader module entry: never recorded, removed, or from a previous generation of
    /// a reused slot.
    /// </exception>
    public ulong GetId(ShaderModuleHandle handle) => Find(BackendObjectKind.ShaderModule, handle.Value);

    /// <summary>Resolves a bind group layout handle to the id its backend returned.</summary>
    /// <param name="handle">A live bind group layout handle of the device that owns this table.</param>
    /// <returns>The layout's backend id; never 0.</returns>
    /// <exception cref="ArgumentException"><paramref name="handle"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="handle"/> has no bind group layout entry: never recorded, removed, or from a previous generation
    /// of a reused slot.
    /// </exception>
    public ulong GetId(BindGroupLayoutHandle handle) => Find(BackendObjectKind.BindGroupLayout, handle.Value);

    /// <summary>Resolves a pipeline layout handle to the id its backend returned.</summary>
    /// <param name="handle">A live pipeline layout handle of the device that owns this table.</param>
    /// <returns>The layout's backend id; never 0.</returns>
    /// <exception cref="ArgumentException"><paramref name="handle"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="handle"/> has no pipeline layout entry: never recorded, removed, or from a previous generation
    /// of a reused slot.
    /// </exception>
    public ulong GetId(PipelineLayoutHandle handle) => Find(BackendObjectKind.PipelineLayout, handle.Value);

    /// <summary>Resolves a bind group handle, persistent or frame-lifetime, to the id its backend returned.</summary>
    /// <param name="handle">A live bind group handle of the device that owns this table.</param>
    /// <returns>The bind group's backend id; never 0.</returns>
    /// <exception cref="ArgumentException"><paramref name="handle"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="handle"/> has no bind group entry: never recorded, removed, or from a previous generation of a
    /// reused slot.
    /// </exception>
    public ulong GetId(BindGroupHandle handle) => Find(BackendObjectKind.BindGroup, handle.Value);

    /// <summary>Resolves a graphics pipeline handle to the id its backend returned.</summary>
    /// <param name="handle">A live graphics pipeline handle of the device that owns this table.</param>
    /// <returns>The pipeline's backend id; never 0.</returns>
    /// <exception cref="ArgumentException"><paramref name="handle"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="handle"/> has no graphics pipeline entry: never recorded, removed, or from a previous generation
    /// of a reused slot.
    /// </exception>
    public ulong GetId(GraphicsPipelineHandle handle) => Find(BackendObjectKind.GraphicsPipeline, handle.Value);

    /// <summary>Resolves a compute pipeline handle to the id its backend returned.</summary>
    /// <param name="handle">A live compute pipeline handle of the device that owns this table.</param>
    /// <returns>The pipeline's backend id; never 0.</returns>
    /// <exception cref="ArgumentException"><paramref name="handle"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="handle"/> has no compute pipeline entry: never recorded, removed, or from a previous generation
    /// of a reused slot.
    /// </exception>
    public ulong GetId(ComputePipelineHandle handle) => Find(BackendObjectKind.ComputePipeline, handle.Value);

    /// <summary>Returns the name of an interned debug label, as UTF-8.</summary>
    /// <param name="label">A label of the device that owns this table, or <c>default</c> for no label.</param>
    /// <returns>
    /// The name's UTF-8 bytes, without a NUL terminator; empty for <c>default</c>. The bytes stay valid and unchanged for the
    /// table's lifetime: labels are never removed, and <see cref="Clear"/> keeps them.
    /// </returns>
    /// <exception cref="StaleHandleException"><paramref name="label"/> isn't null and was never interned in this table.</exception>
    public ReadOnlySpan<byte> GetLabel(DebugLabel label)
    {
        if (label.IsNull)
        {
            return [];
        }

        return _labels.TryGet(label.Value, out byte[]? name) ? name : throw new StaleHandleException(label.Value);
    }

    /// <summary>Records the backend id of a newly created object.</summary>
    /// <param name="kind">The object's kind.</param>
    /// <param name="handle">The object's new handle, whose slot holds no live entry of that kind.</param>
    /// <param name="id">The id the backend returned; non-zero.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> isn't a <see cref="BackendObjectKind"/> member, <paramref name="id"/> is 0, or the handle's index
    /// is beyond any slot a handle allocator issues.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="handle"/> is the null handle.</exception>
    /// <exception cref="InvalidOperationException">The handle's slot already holds a live entry of that kind.</exception>
    /// <exception cref="OutOfMemoryException">The table couldn't grow; it is unchanged.</exception>
    internal void Set(BackendObjectKind kind, Handle handle, ulong id)
    {
        Slots<ulong> slots = Table(kind);
        ThrowIfNull(handle, nameof(handle));
        ArgumentOutOfRangeException.ThrowIfZero(id);
        slots.Set(handle, id, kind, this);
    }

    /// <summary>Removes the entry of a destroyed object.</summary>
    /// <param name="kind">The object's kind.</param>
    /// <param name="handle">The object's handle, which has a live entry of that kind.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> isn't a <see cref="BackendObjectKind"/> member.</exception>
    /// <exception cref="ArgumentException"><paramref name="handle"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException"><paramref name="handle"/> has no live entry of that kind (for example, removed twice).</exception>
    internal void Remove(BackendObjectKind kind, Handle handle)
    {
        Slots<ulong> slots = Table(kind);
        ThrowIfNull(handle, nameof(handle));
        slots.Remove(handle);
    }

    /// <summary>Removes every object entry of every kind, keeping the labels and the storage; called when the device is lost.</summary>
    internal void Clear()
    {
        foreach (Slots<ulong> slots in _objects)
        {
            slots.Clear();
        }
    }

    /// <summary>Interns a debug label's name, stored as UTF-8 (a lone surrogate becomes U+FFFD).</summary>
    /// <param name="label">The new label, which isn't interned yet.</param>
    /// <param name="name">The label's name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="label"/> is the null label.</exception>
    /// <exception cref="InvalidOperationException">The label's slot already holds an interned name.</exception>
    /// <exception cref="OutOfMemoryException">The table couldn't grow; it is unchanged.</exception>
    internal void SetLabel(DebugLabel label, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        ThrowIfNull(label.Value, nameof(label));
        _labels.Set(label.Value, Encoding.UTF8.GetBytes(name), kind: null, this);
    }

    private Slots<ulong> Table(BackendObjectKind kind)
    {
        // An unsigned compare rejects negative values too.
        if ((uint)kind >= KindCount)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a BackendObjectKind member.");
        }

        return _objects[(int)kind];
    }

    private static void ThrowIfNull(Handle handle, string parameter)
    {
        if (handle.IsNull)
        {
            throw new ArgumentException("The handle is null.", parameter);
        }
    }

    private ulong Find(BackendObjectKind kind, Handle handle)
    {
        if (handle.IsNull)
        {
            throw new ArgumentException($"The {kind} handle is null.", nameof(handle));
        }

        return _objects[(int)kind].TryGet(handle, out ulong id) ? id : throw new StaleHandleException(handle);
    }

    /// <summary>One table: per slot index, the raw handle that owns the entry (0 when free) and its value.</summary>
    private sealed class Slots<TValue>
    {
        private ulong[] _handles = [];
        private TValue[] _values = [];

        public bool TryGet(Handle handle, out TValue value)
        {
            // A free slot stores raw 0, the null handle's value, so the compare below would match null; every caller
            // (GetLabel, Remove, Find) rejects the null handle first.
            Debug.Assert(!handle.IsNull, "Slots.TryGet must never see the null handle.");
            uint index = handle.Index;
            if (index < (uint)_handles.Length && _handles[index] == handle.RawValue)
            {
                value = _values[index];
                return true;
            }

            value = default!;
            return false;
        }

        public void Set(Handle handle, TValue value, BackendObjectKind? kind, BackendObjectTable owner)
        {
            uint index = handle.Index;
            if (index >= HandleAllocator.MaxSlotLimit)
            {
                throw new ArgumentOutOfRangeException(nameof(handle), handle, "The handle's index is beyond any slot a handle allocator issues.");
            }

            if (index < (uint)_handles.Length && _handles[index] != 0)
            {
                throw new InvalidOperationException($"{handle}: its slot already holds {Handle.FromRawValue(_handles[index])}.");
            }

            if (index >= (uint)_handles.Length)
            {
                Grow((int)index + 1, kind, owner);
            }

            _handles[index] = handle.RawValue;
            _values[index] = value;
        }

        public void Remove(Handle handle)
        {
            if (!TryGet(handle, out _))
            {
                throw new StaleHandleException(handle);
            }

            _handles[handle.Index] = 0;
            _values[handle.Index] = default!;
        }

        public void Clear()
        {
            Array.Clear(_handles);
            Array.Clear(_values);
        }

        private void Grow(int minLength, BackendObjectKind? kind, BackendObjectTable owner)
        {
            long doubled = Math.Max((long)_handles.Length * 2, InitialCapacity);
            int length = (int)Math.Min(Math.Max(doubled, minLength), HandleAllocator.MaxSlotLimit);

            // Allocate both arrays before replacing either: running out of memory part-way leaves the table unchanged.
            owner.BeforeGrowth?.Invoke(kind);
            ulong[] handles = new ulong[length];
            owner.BeforeGrowth?.Invoke(kind);
            var values = new TValue[length];
            _handles.CopyTo(handles, 0);
            _values.CopyTo(values, 0);
            _handles = handles;
            _values = values;
        }
    }
}
