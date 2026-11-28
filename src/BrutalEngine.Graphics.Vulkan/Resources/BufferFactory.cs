using System;
using System.Text.Unicode;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Vulkan.Devices;
using BrutalEngine.Graphics.Vulkan.Memory;

namespace BrutalEngine.Graphics.Vulkan.Resources;

/// <summary>
/// Creates and destroys a device's buffers through VMA (docs/design/graphics-vulkan.md section 6) and keeps them in the
/// buffer kind's <see cref="ObjectTable{TEntry}"/>, as <c>IBackendDevice.CreateBuffer</c> and <c>Destroy</c> need.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Create"/> maps the usage one to one (<see cref="BufferUsageMapping"/>), places the memory as
/// <see cref="MemoryPlacement"/> gives it, and calls <c>vmaCreateBuffer</c> (<c>Brutal.Vulkan.Vma/Generated/Vma.gen.cs:336</c>)
/// with exclusive sharing, through the device's gate: an injected fault replaces the call, so nothing is created behind
/// it. Out of host or device memory is <see cref="ErrorKind.OutOfMemory"/>, a lost device
/// <see cref="ErrorKind.Unavailable"/> (and marked on the gate), anything else <see cref="ErrorKind.Native"/>, each with
/// the code; a failure leaves nothing to destroy.
/// </para>
/// <para>
/// A buffer in <see cref="BackendMemory.HostUpload"/> or <see cref="BackendMemory.HostReadback"/> memory returns VMA's
/// persistent mapping (<c>pMappedData</c>), valid until the buffer is destroyed; the device kinds return 0 whatever VMA
/// reports.
/// </para>
/// <para>
/// A description's debug name is applied once, at creation, to the Vulkan object (<see cref="DebugNames"/>, best effort
/// and only with <c>VK_EXT_debug_utils</c>) and to the VMA allocation (<c>vmaSetAllocationName</c>,
/// <c>Brutal.Vulkan.Vma/Generated/Vma.gen.cs:197</c>, which copies it): as UTF-8 of at most
/// <see cref="DebugNames.BufferSize"/> minus one bytes, cut at a character boundary. A null or empty name names nothing.
/// </para>
/// <para>
/// Not thread-safe: the device's owner thread uses it. Once the table has grown, a successful <see cref="Create"/> and
/// <see cref="Destroy"/> allocate nothing.
/// </para>
/// </remarks>
internal sealed class BufferFactory
{
    private readonly INative _native;
    private readonly DebugNames.Naming _naming;
    private readonly VulkanCallGate _gate;

    /// <summary>Creates the factory of a live device.</summary>
    /// <param name="core">The device; it outlives the factory.</param>
    /// <param name="vma">The device's allocator; it outlives every buffer.</param>
    /// <param name="gate">The device's gate.</param>
    /// <exception cref="ArgumentNullException"><paramref name="core"/> or <paramref name="gate"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="vma"/> is the null handle.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="core"/> is disposed.</exception>
    public BufferFactory(VulkanDeviceCore core, VmaHandle vma, VulkanCallGate gate)
        : this(new NativeBuffers(Live(core).Device, vma), core.Naming, gate, new ObjectTable<Entry>())
    {
    }

    /// <summary>Creates a factory over a given native seam and table, so tests can observe or script the calls.</summary>
    /// <param name="native">Makes the native calls.</param>
    /// <param name="naming">The device's naming state.</param>
    /// <param name="gate">The device's gate.</param>
    /// <param name="table">The table buffers are kept in.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    internal BufferFactory(INative native, DebugNames.Naming naming, VulkanCallGate gate, ObjectTable<Entry> table)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _naming = naming ?? throw new ArgumentNullException(nameof(naming));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        Table = table ?? throw new ArgumentNullException(nameof(table));
    }

    /// <summary>Gets the live buffers, by id.</summary>
    public ObjectTable<Entry> Table { get; }

    /// <summary>Creates a buffer and keeps it in <see cref="Table"/>.</summary>
    /// <param name="description">The description, already validated by the front end.</param>
    /// <param name="memory">Where the buffer lives.</param>
    /// <returns>The buffer's id and, for host memory, its mapped pointer; or the error, as the remarks give it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The size isn't positive, the usage has an undefined bit, or <paramref name="memory"/> isn't a
    /// <see cref="BackendMemory"/> member; checked before any native call.
    /// </exception>
    /// <exception cref="InvalidOperationException">The table can't issue another id; the new buffer is destroyed again.</exception>
    public unsafe Result<BackendBuffer> Create(in BufferDescription description, BackendMemory memory)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(description.Size);
        VkBufferCreateInfo info = new()
        {
            Size = new ByteSize64((ulong)description.Size),
            Usage = BufferUsageMapping.Map(description.Usage),
            SharingMode = VkSharingMode.Exclusive,
        };
        VmaAllocationCreateInfo placement = MemoryPlacement.For(memory);
        VkBuffer buffer = default;
        VmaAllocation allocation = default;
        VmaAllocationInfo allocated = default;

        // Sound: every pointer is to a local of this frame, valid for the whole call; VMA copies the descriptions.
        VkResult result = _gate.TryTakeFault(VulkanCall.CreateBuffer, out VkResult injected)
            ? injected
            : _native.CreateBuffer(&info, &placement, &buffer, &allocation, &allocated);
        if (_gate.Check(result, VulkanCall.CreateBuffer).TryGetError(out EngineError error))
        {
            return error;
        }

        ulong id;
        try
        {
            id = Table.Add(new Entry(buffer, allocation, memory, description.DebugName));
        }
        catch
        {
            _native.DestroyBuffer(buffer, allocation);
            throw;
        }

        Name(buffer, allocation, description.DebugName);
        bool host = memory is BackendMemory.HostUpload or BackendMemory.HostReadback;
        return new BackendBuffer(id, host ? (nint)allocated.PMappedData.Pointer : 0);
    }

    /// <summary>Destroys a buffer and its allocation together (<c>vmaDestroyBuffer</c>) and removes it from <see cref="Table"/>.</summary>
    /// <param name="id">The buffer's id.</param>
    /// <returns>Whether <paramref name="id"/> was live; a stale or zero id destroys nothing.</returns>
    public bool Destroy(ulong id)
    {
        if (!Table.TryRemove(id, out Entry entry))
        {
            return false;
        }

        _native.DestroyBuffer(entry.Buffer, entry.Allocation);
        return true;
    }

    private static VulkanDeviceCore Live(VulkanDeviceCore core) => core ?? throw new ArgumentNullException(nameof(core));

    private unsafe void Name(VkBuffer buffer, VmaAllocation allocation, string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        // Sound: the buffer is a stack local, valid for both calls, which copy the name. Conversion stops at the last
        // whole character that fits before the NUL.
        byte* utf8 = stackalloc byte[DebugNames.BufferSize];
        _ = Utf8.FromUtf16(name, new Span<byte>(utf8, DebugNames.BufferSize - 1), out _, out int written);
        utf8[written] = 0;
        DebugNames.Set(_native, _naming, VkObjectType.Buffer, (ulong)buffer.VkHandle, new ReadOnlySpan<byte>(utf8, written));
        _native.SetAllocationName(allocation, utf8);
    }

    /// <summary>A live buffer: its handles, its memory kind and its debug name.</summary>
    /// <param name="Buffer">The buffer.</param>
    /// <param name="Allocation">Its VMA allocation.</param>
    /// <param name="Memory">The memory kind it was created in.</param>
    /// <param name="Name">The description's debug name, for the leak report.</param>
    internal readonly record struct Entry(VkBuffer Buffer, VmaAllocation Allocation, BackendMemory Memory, string? Name);

    /// <summary>Makes the native calls, so tests can observe them or script their results.</summary>
    internal unsafe interface INative : DebugNames.INamer
    {
        /// <summary>Calls <c>vmaCreateBuffer</c>.</summary>
        /// <param name="info">The buffer's description; valid during the call only.</param>
        /// <param name="placement">The allocation's description; valid during the call only.</param>
        /// <param name="buffer">Receives the buffer.</param>
        /// <param name="allocation">Receives the allocation.</param>
        /// <param name="allocated">Receives where VMA placed it, with the mapping.</param>
        /// <returns>VMA's result.</returns>
        public VkResult CreateBuffer(VkBufferCreateInfo* info, VmaAllocationCreateInfo* placement, VkBuffer* buffer, VmaAllocation* allocation, VmaAllocationInfo* allocated);

        /// <summary>Calls <c>vmaDestroyBuffer</c> (<c>Brutal.Vulkan.Vma/Generated/Vma.gen.cs:385</c>).</summary>
        /// <param name="buffer">The buffer.</param>
        /// <param name="allocation">Its allocation.</param>
        public void DestroyBuffer(VkBuffer buffer, VmaAllocation allocation);

        /// <summary>Calls <c>vmaSetAllocationName</c>.</summary>
        /// <param name="allocation">The allocation.</param>
        /// <param name="name">The NUL-terminated UTF-8 name; valid during the call only.</param>
        public void SetAllocationName(VmaAllocation allocation, byte* name);
    }

    /// <summary>The real calls: BRUTAL's generated VMA binding and the device's raw function table.</summary>
    /// <param name="device">The live device.</param>
    /// <param name="vma">The device's allocator.</param>
    internal sealed unsafe class NativeBuffers(Device device, VmaHandle vma) : INative
    {
        private readonly VmaAllocatorHandle _vma = vma.IsNull
            ? throw new ArgumentException("The allocator is the null handle.", nameof(vma))
            : vma.VkHandle;

        /// <inheritdoc/>
        public VkResult CreateBuffer(VkBufferCreateInfo* info, VmaAllocationCreateInfo* placement, VkBuffer* buffer, VmaAllocation* allocation, VmaAllocationInfo* allocated) =>
            Vma.PInvoke.vmaCreateBuffer(_vma, info, placement, buffer, allocation, allocated);

        /// <inheritdoc/>
        public void DestroyBuffer(VkBuffer buffer, VmaAllocation allocation) => Vma.PInvoke.vmaDestroyBuffer(_vma, buffer, allocation);

        /// <inheritdoc/>
        public void SetAllocationName(VmaAllocation allocation, byte* name) => Vma.PInvoke.vmaSetAllocationName(_vma, allocation, name);

        /// <inheritdoc/>
        public VkResult SetName(VkDebugUtilsObjectNameInfoEXT* info) => new DebugNames.DeviceNamer(device).SetName(info);
    }
}
