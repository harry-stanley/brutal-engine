using System;
using System.Text.Unicode;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Vulkan.Devices;
using BrutalEngine.Graphics.Vulkan.Memory;

namespace BrutalEngine.Graphics.Vulkan.Resources;

/// <summary>
/// Creates and destroys a device's 2D textures through VMA (docs/design/graphics-vulkan.md section 6, "Textures") and
/// keeps them in the texture kind's <see cref="ObjectTable{TEntry}"/>, as <c>IBackendDevice.CreateTexture</c> and
/// <c>Destroy</c> need.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Create"/> describes a 2D image with <c>OPTIMAL</c> tiling, <c>EXCLUSIVE</c> sharing and initial layout
/// <c>UNDEFINED</c>: the format from <see cref="FormatTable"/>, the usage from <see cref="TextureUsageMapping"/>, and the
/// size, mip levels, layers and sample count as given. Before VMA is called, in this order:
/// </para>
/// <list type="number">
/// <item><description>A sample count above 1 with more than one layer is <see cref="ErrorKind.Unsupported"/>
/// (<see cref="MultisampleArrayRestricted"/>) when the portability subset restricts <c>multisampleArrayImage</c>
/// (section 3), before any native call.</description></item>
/// <item><description><c>vkGetPhysicalDeviceImageFormatProperties2</c>
/// (<c>Brutal.Vulkan/Generated/Handles/VkInstance.gen.cs:62</c>) checks the format, type, tiling and usage, through the
/// device's gate: <c>VK_ERROR_FORMAT_NOT_SUPPORTED</c> is <see cref="ErrorKind.Unsupported"/>, and any other failure the
/// gate's kind.</description></item>
/// <item><description>A size above the returned <c>maxExtent</c>, mip levels above <c>maxMipLevels</c>, layers above
/// <c>maxArrayLayers</c>, or a sample count outside <c>sampleCounts</c> is <see cref="ErrorKind.Unsupported"/>
/// (<see cref="ExceedsFormatLimits"/>).</description></item>
/// </list>
/// <para>
/// Then <c>vmaCreateImage</c> (<c>Brutal.Vulkan.Vma/Generated/Vma.gen.cs:392</c>) places the image as
/// <see cref="BackendMemory.DeviceLocal"/> (<see cref="MemoryPlacement"/>), through the gate: out of host or device memory
/// is <see cref="ErrorKind.OutOfMemory"/> and first logs each heap's usage and budget at <c>Warning</c>
/// (<see cref="HeapBudgetLog"/>), a lost device <see cref="ErrorKind.Unavailable"/> (and marked on the gate), anything
/// else <see cref="ErrorKind.Native"/>, each with the code. An injected fault replaces either call, so nothing is created
/// behind it. A failure leaves nothing to destroy.
/// </para>
/// <para>
/// A description's debug name is applied once, at creation, to the image (<see cref="DebugNames"/>, best effort and only
/// with <c>VK_EXT_debug_utils</c>) and to the VMA allocation (<c>vmaSetAllocationName</c>, which copies it): as UTF-8 of
/// at most <see cref="DebugNames.BufferSize"/> minus one bytes, cut at a character boundary. A null or empty name names
/// nothing.
/// </para>
/// <para>Not thread-safe: the device's owner thread uses it. <see cref="Create"/> may allocate (the table grows).</para>
/// </remarks>
internal sealed class TextureFactory
{
    /// <summary>The message of a multisampled array texture the portability subset forbids.</summary>
    internal const string MultisampleArrayRestricted =
        "The device's portability subset can't create a multisampled texture with several layers (multisampleArrayImage)";

    /// <summary>The message of a texture beyond its format's limits on the device.</summary>
    internal const string ExceedsFormatLimits =
        "vkGetPhysicalDeviceImageFormatProperties2: the format can't have this size, mip level count, layer count or sample count on this device";

    private readonly INative _native;
    private readonly DebugNames.Naming _naming;
    private readonly VulkanCallGate _gate;
    private readonly PortabilityRestrictions _portability;
    private readonly LogCategory _log;

    /// <summary>Creates the factory of a live device.</summary>
    /// <param name="core">The device; it outlives the factory. Its portability restrictions apply.</param>
    /// <param name="vma">The device's allocator; it outlives every texture.</param>
    /// <param name="gate">The device's gate.</param>
    /// <param name="log">Where out-of-memory budgets are logged (the device's <c>Graphics.Vulkan</c> category); <see langword="default"/> for nowhere.</param>
    /// <exception cref="ArgumentNullException"><paramref name="core"/> or <paramref name="gate"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="vma"/> is the null handle.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="core"/> is disposed.</exception>
    public TextureFactory(VulkanDeviceCore core, VmaHandle vma, VulkanCallGate gate, LogCategory log)
        : this(new NativeTextures(Live(core).Device, vma), core.Naming, gate, core.Portability, new ObjectTable<Entry>(), log)
    {
    }

    /// <summary>Creates a factory over a given native seam, restrictions and table, so tests can observe or script the calls.</summary>
    /// <param name="native">Makes the native calls.</param>
    /// <param name="naming">The device's naming state.</param>
    /// <param name="gate">The device's gate.</param>
    /// <param name="portability">The device's portability restrictions.</param>
    /// <param name="table">The table textures are kept in.</param>
    /// <param name="log">Where out-of-memory budgets are logged.</param>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    internal TextureFactory(INative native, DebugNames.Naming naming, VulkanCallGate gate, PortabilityRestrictions portability, ObjectTable<Entry> table, LogCategory log)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _naming = naming ?? throw new ArgumentNullException(nameof(naming));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _portability = portability;
        Table = table ?? throw new ArgumentNullException(nameof(table));
        _log = log;
    }

    /// <summary>Gets the live textures, by id.</summary>
    public ObjectTable<Entry> Table { get; }

    /// <summary>Creates a texture and keeps it in <see cref="Table"/>.</summary>
    /// <param name="description">The description, already validated by the front end.</param>
    /// <returns>The texture's id, or the error, as the remarks give it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The format isn't one of the 16 formats, the usage has an undefined bit, a side, the mip levels or the layers aren't
    /// positive, or the sample count isn't 1, 2, 4, 8 or 16; checked before any native call.
    /// </exception>
    /// <exception cref="InvalidOperationException">The table can't issue another id; the new texture is destroyed again.</exception>
    public unsafe Result<ulong> Create(in TextureDescription description)
    {
        VkImageCreateInfo info = Describe(description);
        if (description.SampleCount > 1 && description.ArrayLayers > 1 && !_portability.MultisampleArrayImage)
        {
            return new EngineError(ErrorKind.Unsupported, MultisampleArrayRestricted);
        }

        if (CheckSupport(info).TryGetError(out EngineError unsupported))
        {
            return unsupported;
        }

        VmaAllocationCreateInfo placement = MemoryPlacement.For(BackendMemory.DeviceLocal);
        VkImage image = default;
        VmaAllocation allocation = default;

        // Sound: every pointer is to a local of this frame, valid for the whole call; VMA copies the descriptions.
        VkResult result = _gate.TryTakeFault(VulkanCall.CreateImage, out VkResult injected)
            ? injected
            : _native.CreateImage(&info, &placement, &image, &allocation);
        if (_gate.Check(result, VulkanCall.CreateImage).TryGetError(out EngineError error))
        {
            if (error.Kind == ErrorKind.OutOfMemory)
            {
                _native.WriteBudgets(_log);
            }

            return error;
        }

        ulong id;
        try
        {
            id = Table.Add(new Entry(image, allocation, description));
        }
        catch
        {
            _native.DestroyImage(image, allocation);
            throw;
        }

        Name(image, allocation, description.DebugName);
        return id;
    }

    /// <summary>Destroys a texture and its allocation together (<c>vmaDestroyImage</c>) and removes it from <see cref="Table"/>.</summary>
    /// <param name="id">The texture's id.</param>
    /// <returns>Whether <paramref name="id"/> was live; a stale or zero id destroys nothing.</returns>
    public bool Destroy(ulong id)
    {
        if (!Table.TryRemove(id, out Entry entry))
        {
            return false;
        }

        _native.DestroyImage(entry.Image, entry.Allocation);
        return true;
    }

    private static VulkanDeviceCore Live(VulkanDeviceCore core) => core ?? throw new ArgumentNullException(nameof(core));

    private static VkImageCreateInfo Describe(in TextureDescription description)
    {
        VkFormat format = FormatTable.ToVk(description.Format);
        VkImageUsageFlags usage = TextureUsageMapping.Map(description.Usage);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(description.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(description.Height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(description.MipLevels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(description.ArrayLayers);
        int samples = description.SampleCount;
        if (samples is not (1 or 2 or 4 or 8 or 16))
        {
            throw new ArgumentOutOfRangeException(nameof(description) + "." + nameof(TextureDescription.SampleCount), samples, "Not 1, 2, 4, 8 or 16.");
        }

        return new VkImageCreateInfo
        {
            ImageType = VkImageType._2D,
            Format = format,
            Extent = new VkExtent3D { Width = description.Width, Height = description.Height, Depth = 1 },
            MipLevels = description.MipLevels,
            ArrayLayers = description.ArrayLayers,

            // VK_SAMPLE_COUNT_n_BIT is n for each allowed count (Brutal.Vulkan/Generated/Enums/VkSampleCountFlags.gen.cs:34-54).
            Samples = (VkSampleCountFlags)samples,
            Tiling = VkImageTiling.Optimal,
            Usage = usage,
            SharingMode = VkSharingMode.Exclusive,
            InitialLayout = VkImageLayout.Undefined,
        };
    }

    private unsafe Result CheckSupport(in VkImageCreateInfo info)
    {
        VkPhysicalDeviceImageFormatInfo2 query = new() { Format = info.Format, Type = info.ImageType, Tiling = info.Tiling, Usage = info.Usage, Flags = info.Flags };
        VkImageFormatProperties2 properties = new();

        // Sound: both structures are locals of this frame, valid for the whole call, which fills properties in place.
        VkResult result = _gate.TryTakeFault(VulkanCall.GetImageFormatProperties2, out VkResult injected)
            ? injected
            : _native.GetImageFormatProperties(&query, &properties);
        if (_gate.Check(result, VulkanCall.GetImageFormatProperties2).TryGetError(out EngineError error))
        {
            return error;
        }

        VkImageFormatProperties limits = properties.ImageFormatProperties;
        bool fits = info.Extent.Width <= limits.MaxExtent.Width
            && info.Extent.Height <= limits.MaxExtent.Height
            && info.MipLevels <= limits.MaxMipLevels
            && info.ArrayLayers <= limits.MaxArrayLayers
            && (limits.SampleCounts & info.Samples) != 0;
        return fits ? Result.Success : new EngineError(ErrorKind.Unsupported, ExceedsFormatLimits);
    }

    private unsafe void Name(VkImage image, VmaAllocation allocation, string? name)
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
        DebugNames.Set(_native, _naming, VkObjectType.Image, (ulong)image.VkHandle, new ReadOnlySpan<byte>(utf8, written));
        _native.SetAllocationName(allocation, utf8);
    }

    /// <summary>A live texture: its handles and the description it was created from, debug name included.</summary>
    /// <param name="Image">The image.</param>
    /// <param name="Allocation">Its VMA allocation.</param>
    /// <param name="Description">Its description: format, size, mip levels, layers, samples, usage and debug name.</param>
    internal readonly record struct Entry(VkImage Image, VmaAllocation Allocation, TextureDescription Description);

    /// <summary>Makes the native calls, so tests can observe them or script their results.</summary>
    internal unsafe interface INative : DebugNames.INamer
    {
        /// <summary>Calls <c>vkGetPhysicalDeviceImageFormatProperties2</c> on the device's adapter.</summary>
        /// <param name="info">The combination to check; valid during the call only.</param>
        /// <param name="properties">Receives the combination's limits.</param>
        /// <returns>The command's result.</returns>
        public VkResult GetImageFormatProperties(VkPhysicalDeviceImageFormatInfo2* info, VkImageFormatProperties2* properties);

        /// <summary>Calls <c>vmaCreateImage</c>, without allocation information.</summary>
        /// <param name="info">The image's description; valid during the call only.</param>
        /// <param name="placement">The allocation's description; valid during the call only.</param>
        /// <param name="image">Receives the image.</param>
        /// <param name="allocation">Receives the allocation.</param>
        /// <returns>VMA's result.</returns>
        public VkResult CreateImage(VkImageCreateInfo* info, VmaAllocationCreateInfo* placement, VkImage* image, VmaAllocation* allocation);

        /// <summary>Calls <c>vmaDestroyImage</c> (<c>Brutal.Vulkan.Vma/Generated/Vma.gen.cs:430</c>).</summary>
        /// <param name="image">The image.</param>
        /// <param name="allocation">Its allocation.</param>
        public void DestroyImage(VkImage image, VmaAllocation allocation);

        /// <summary>Calls <c>vmaSetAllocationName</c>.</summary>
        /// <param name="allocation">The allocation.</param>
        /// <param name="name">The NUL-terminated UTF-8 name; valid during the call only.</param>
        public void SetAllocationName(VmaAllocation allocation, byte* name);

        /// <summary>Logs every heap's usage and budget (<see cref="HeapBudgetLog.Write(VmaHandle, LogCategory)"/>).</summary>
        /// <param name="log">Where the warnings go.</param>
        public void WriteBudgets(LogCategory log);
    }

    /// <summary>The real calls: BRUTAL's generated VMA binding and the raw instance and device function tables.</summary>
    /// <param name="device">The live device.</param>
    /// <param name="vma">The device's allocator.</param>
    internal sealed unsafe class NativeTextures(Device device, VmaHandle vma) : INative
    {
        private readonly VmaHandle _vma = vma.IsNull ? throw new ArgumentException("The allocator is the null handle.", nameof(vma)) : vma;

        /// <inheritdoc/>
        public VkResult GetImageFormatProperties(VkPhysicalDeviceImageFormatInfo2* info, VkImageFormatProperties2* properties)
        {
            PhysicalDevice adapter = device.PhysicalDevice;
            return adapter.PInvoke.vkGetPhysicalDeviceImageFormatProperties2.Invoke(adapter.Handle, info, properties);
        }

        /// <inheritdoc/>
        public VkResult CreateImage(VkImageCreateInfo* info, VmaAllocationCreateInfo* placement, VkImage* image, VmaAllocation* allocation) =>
            Vma.PInvoke.vmaCreateImage(_vma.VkHandle, info, placement, image, allocation, (VmaAllocationInfo*)null);

        /// <inheritdoc/>
        public void DestroyImage(VkImage image, VmaAllocation allocation) => Vma.PInvoke.vmaDestroyImage(_vma.VkHandle, image, allocation);

        /// <inheritdoc/>
        public void SetAllocationName(VmaAllocation allocation, byte* name) => Vma.PInvoke.vmaSetAllocationName(_vma.VkHandle, allocation, name);

        /// <inheritdoc/>
        public void WriteBudgets(LogCategory log) => HeapBudgetLog.Write(_vma, log);

        /// <inheritdoc/>
        public VkResult SetName(VkDebugUtilsObjectNameInfoEXT* info) => new DebugNames.DeviceNamer(device).SetName(info);
    }
}
