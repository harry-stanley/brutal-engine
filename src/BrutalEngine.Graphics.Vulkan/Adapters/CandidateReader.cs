using System;
using System.Text;
using Brutal;
using Brutal.VulkanApi;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Vulkan.Instances;

namespace BrutalEngine.Graphics.Vulkan.Adapters;

/// <summary>
/// Reads one physical device into an <see cref="AdapterCandidate"/> (docs/design/graphics-vulkan.md section 3,
/// "Candidates"): its properties, driver, features, device extensions and queue families, through the raw instance
/// function table.
/// </summary>
/// <remarks>
/// <para>
/// <c>VkPhysicalDeviceDriverProperties</c> is chained only for a device that reports Vulkan 1.2 or later (it is core
/// there), and <c>VkPhysicalDeviceVulkan13Features</c> only for one that reports 1.3 or later; below those versions the
/// driver ID stays 0 and <see cref="AdapterCandidate.DynamicRendering"/> and <see cref="AdapterCandidate.Synchronization2"/>
/// stay <see langword="false"/>. Every chained structure is a stack local that outlives the query.
/// </para>
/// <para>
/// Device extensions use the two-call pattern with the bounded <c>VK_INCOMPLETE</c> retry of the instance code
/// (<see cref="InstanceExtensions.MaxEnumerationAttempts"/>), because BRUTAL's <c>PhysicalDevice.ExtensionNames</c> throws
/// on a failed enumeration. Present support is asked of every queue family only when there is a surface, and is
/// <see langword="false"/> otherwise. Both result-returning calls pass through the gate, so the fault injector reaches them.
/// </para>
/// <para>
/// Creation-time only, on the thread that enumerates: allocates the candidate's name, extension names and arrays. Not
/// thread-safe beyond what the gate allows.
/// </para>
/// </remarks>
internal static class CandidateReader
{
    /// <summary>Reads a physical device of a live instance.</summary>
    /// <param name="device">The device, from BRUTAL's <c>Instance.PhysicalDevices</c>.</param>
    /// <param name="surface">The surface to ask for present support, or the null handle for none.</param>
    /// <param name="gate">The gate every result-returning call passes through.</param>
    /// <returns>The candidate, or the first failed call's error (<see cref="VkCheck.Error"/>).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> or <paramref name="gate"/> is <see langword="null"/>.</exception>
    public static Result<AdapterCandidate> Read(PhysicalDevice device, VkSurfaceKHR surface, VulkanCallGate gate)
    {
        ArgumentNullException.ThrowIfNull(device);
        return Read(new DeviceSource(device, surface), gate);
    }

    /// <summary><see cref="Read(PhysicalDevice, VkSurfaceKHR, VulkanCallGate)"/> over given device queries.</summary>
    /// <typeparam name="TSource">The device queries; the real ones are a struct, so nothing is boxed.</typeparam>
    /// <param name="source">The device queries.</param>
    /// <param name="gate">The gate every result-returning call passes through.</param>
    /// <returns>As <see cref="Read(PhysicalDevice, VkSurfaceKHR, VulkanCallGate)"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="gate"/> is <see langword="null"/>.</exception>
    internal static unsafe Result<AdapterCandidate> Read<TSource>(TSource source, VulkanCallGate gate)
        where TSource : ISource
    {
        ArgumentNullException.ThrowIfNull(gate);

        // Sound: properties, driver, vulkan13 and features are stack locals of this method, so their addresses (the
        // queries' arguments and pNext chains) stay valid until it returns; the queries fill them in place and keep no
        // pointer. Each chained structure is one the device's reported version supports.
        VkPhysicalDeviceProperties2 properties = new();
        source.GetProperties2(&properties);
        int version = properties.Properties.ApiVersion;
        int major = global::Brutal.VulkanApi.Vulkan.ApiVersionMajor(version);
        int minor = global::Brutal.VulkanApi.Vulkan.ApiVersionMinor(version);
        VkPhysicalDeviceDriverProperties driver = new();
        if (major > 1 || (major == 1 && minor >= 2))
        {
            properties.Next = &driver;
            source.GetProperties2(&properties);
        }

        VkPhysicalDeviceVulkan13Features vulkan13 = new();
        VkPhysicalDeviceFeatures2 features = new() { Next = major > 1 || (major == 1 && minor >= 3) ? &vulkan13 : null };
        source.GetFeatures2(&features);

        Result<string[]> extensions = ReadExtensions(source, gate);
        if (!extensions.TryGetValue(out string[]? names))
        {
            return extensions.Error;
        }

        Result<AdapterCandidate.QueueFamily[]> families = ReadFamilies(source, gate);
        if (!families.TryGetValue(out AdapterCandidate.QueueFamily[]? queueFamilies))
        {
            return families.Error;
        }

        return new AdapterCandidate
        {
            Name = NameOf(properties.Properties.DeviceName, VK.MAX_PHYSICAL_DEVICE_NAME_SIZE),
            Type = TypeOf(properties.Properties.DeviceType),
            Driver = driver.DriverID,
            ApiVersionMajor = major,
            ApiVersionMinor = minor,
            ApiVersionPatch = global::Brutal.VulkanApi.Vulkan.ApiVersionPatch(version),
            DynamicRendering = vulkan13.DynamicRendering,
            Synchronization2 = vulkan13.Synchronization2,
            SamplerAnisotropy = features.Features.SamplerAnisotropy,
            TextureCompressionBC = features.Features.TextureCompressionBC,
            TextureCompressionAstcLdr = features.Features.TextureCompressionASTCLDR,
            TimestampPeriod = properties.Properties.Limits.TimestampPeriod,
            DeviceExtensions = names,
            QueueFamilies = queueFamilies,
        };
    }

    /// <summary>Lists the device's extensions, with the two-call pattern (<see cref="VulkanCall.EnumerateDeviceExtensionProperties"/>).</summary>
    /// <typeparam name="TSource">The device queries.</typeparam>
    /// <param name="source">The device queries.</param>
    /// <param name="gate">The gate both calls pass through.</param>
    /// <returns>
    /// The names in the driver's order; or the gate's failure, including <c>VK_INCOMPLETE</c> after
    /// <see cref="InstanceExtensions.MaxEnumerationAttempts"/> attempts.
    /// </returns>
    internal static unsafe Result<string[]> ReadExtensions<TSource>(TSource source, VulkanCallGate gate)
        where TSource : ISource
    {
        const VulkanCall call = VulkanCall.EnumerateDeviceExtensionProperties;
        VkResult result = VkResult.Incomplete;
        for (int attempt = 0; attempt < InstanceExtensions.MaxEnumerationAttempts && result == VkResult.Incomplete; attempt++)
        {
            int count = 0;
            result = gate.TryTakeFault(call, out VkResult injected) ? injected : source.EnumerateExtensions(ref count, []);
            if (result != VkResult.Success || count == 0)
            {
                break;
            }

            var properties = new VkExtensionProperties[count];
            result = gate.TryTakeFault(call, out injected) ? injected : source.EnumerateExtensions(ref count, properties);
            if (result == VkResult.Success)
            {
                string[] names = new string[count];

                // Sound: the array is pinned while its entries' name buffers are read.
                fixed (VkExtensionProperties* entries = properties)
                {
                    for (int i = 0; i < count; i++)
                    {
                        names[i] = NameOf(entries[i].ExtensionName, VK.MAX_EXTENSION_NAME_SIZE);
                    }
                }

                return names;
            }
        }

        Result checkedResult = gate.Check(result, call);
        return checkedResult.TryGetError(out EngineError error) ? error : Array.Empty<string>();
    }

    // The families in the driver's order, asking each for present support when there is a surface. The count is fixed
    // for a device (a void command), but only the entries the fill wrote are kept.
    private static Result<AdapterCandidate.QueueFamily[]> ReadFamilies<TSource>(TSource source, VulkanCallGate gate)
        where TSource : ISource
    {
        int count = 0;
        source.GetQueueFamilies(ref count, []);
        var properties = new VkQueueFamilyProperties[count];
        source.GetQueueFamilies(ref count, properties);
        var families = new AdapterCandidate.QueueFamily[count];
        for (int i = 0; i < families.Length; i++)
        {
            bool present = false;
            if (source.HasSurface)
            {
                VkResult result = gate.TryTakeFault(VulkanCall.GetSurfaceSupport, out VkResult injected) ? injected : source.GetSurfaceSupport(i, out present);
                Result checkedResult = gate.Check(result, VulkanCall.GetSurfaceSupport);
                if (checkedResult.IsFailure)
                {
                    return checkedResult.Error;
                }
            }

            VkQueueFlags flags = properties[i].QueueFlags;
            families[i] = new(i, (flags & VkQueueFlags.GraphicsBit) != 0, (flags & VkQueueFlags.ComputeBit) != 0, present, properties[i].TimestampValidBits);
        }

        return families;
    }

    // By name: Vulkan's integrated and discrete values are AdapterType's discrete and integrated.
    private static AdapterType TypeOf(VkPhysicalDeviceType type) => type switch
    {
        VkPhysicalDeviceType.IntegratedGpu => AdapterType.Integrated,
        VkPhysicalDeviceType.DiscreteGpu => AdapterType.Discrete,
        VkPhysicalDeviceType.VirtualGpu => AdapterType.Virtual,
        VkPhysicalDeviceType.Cpu => AdapterType.Cpu,
        _ => AdapterType.Other,
    };

    // Sound: name is a fixed buffer of size bytes in a structure the caller has pinned or holds on its stack, and the span
    // covers exactly that buffer, so a name without a terminator is cut at the buffer's end.
    private static unsafe string NameOf(byte* name, int size)
    {
        ReadOnlySpan<byte> bytes = new(name, size);
        int end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
    }

    /// <summary>
    /// The queries one physical device answers, so tests can script a device. They mirror their Vulkan commands; the
    /// enumerations take an empty span to ask for the count and a non-empty one to be filled with up to <c>count</c>
    /// entries.
    /// </summary>
    internal unsafe interface ISource
    {
        /// <summary>Gets whether there is a surface to ask for present support.</summary>
        public bool HasSurface { get; }

        /// <summary><c>vkGetPhysicalDeviceProperties2</c>.</summary>
        /// <param name="properties">The structure to fill, with its chain.</param>
        public void GetProperties2(VkPhysicalDeviceProperties2* properties);

        /// <summary><c>vkGetPhysicalDeviceFeatures2</c>.</summary>
        /// <param name="features">The structure to fill, with its chain.</param>
        public void GetFeatures2(VkPhysicalDeviceFeatures2* features);

        /// <summary><c>vkEnumerateDeviceExtensionProperties</c> with no layer.</summary>
        /// <param name="count">In: the span's capacity (ignored for an empty span). Out: the count, or how many were written.</param>
        /// <param name="properties">Empty to ask for the count; otherwise the entries to fill.</param>
        /// <returns>The command's result.</returns>
        public VkResult EnumerateExtensions(ref int count, Span<VkExtensionProperties> properties);

        /// <summary><c>vkGetPhysicalDeviceQueueFamilyProperties</c>.</summary>
        /// <param name="count">As <see cref="EnumerateExtensions"/>.</param>
        /// <param name="families">As <see cref="EnumerateExtensions"/>.</param>
        public void GetQueueFamilies(ref int count, Span<VkQueueFamilyProperties> families);

        /// <summary><c>vkGetPhysicalDeviceSurfaceSupportKHR</c> against the surface; only called when <see cref="HasSurface"/>.</summary>
        /// <param name="family">The queue family index.</param>
        /// <param name="supported">Whether the family can present to the surface.</param>
        /// <returns>The command's result.</returns>
        public VkResult GetSurfaceSupport(int family, out bool supported);
    }

    /// <summary>
    /// The real queries, through the instance's raw function table (<c>Brutal.Vulkan/Generated/Handles/VkInstance.gen.cs:51</c>,
    /// <c>:55</c>, <c>:59</c>, <c>:60</c>, <c>:71</c>), which BRUTAL's <c>PhysicalDevice.PInvoke</c> shares.
    /// </summary>
    /// <param name="device">The device; its instance outlives this value.</param>
    /// <param name="surface">The surface, or the null handle; created from the same instance with <c>VK_KHR_surface</c> enabled.</param>
    internal readonly unsafe struct DeviceSource(PhysicalDevice device, VkSurfaceKHR surface) : ISource
    {
        /// <inheritdoc/>
        public bool HasSurface => !surface.IsNull();

        // Sound, for every member: the pointers are to the caller's stack locals or to spans pinned here, which outlive
        // the call, and the counts say how many entries the spans hold (an empty span pins as null, the count query).
        // The surface entry point is loaded only with VK_KHR_surface enabled, and is called only with a surface.

        /// <inheritdoc/>
        public void GetProperties2(VkPhysicalDeviceProperties2* properties) =>
            device.PInvoke.vkGetPhysicalDeviceProperties2.Invoke(device.Handle, properties);

        /// <inheritdoc/>
        public void GetFeatures2(VkPhysicalDeviceFeatures2* features) =>
            device.PInvoke.vkGetPhysicalDeviceFeatures2.Invoke(device.Handle, features);

        /// <inheritdoc/>
        public VkResult EnumerateExtensions(ref int count, Span<VkExtensionProperties> properties)
        {
            fixed (int* pCount = &count)
            fixed (VkExtensionProperties* pProperties = properties)
            {
                return device.PInvoke.vkEnumerateDeviceExtensionProperties.Invoke(device.Handle, null, pCount, pProperties);
            }
        }

        /// <inheritdoc/>
        public void GetQueueFamilies(ref int count, Span<VkQueueFamilyProperties> families)
        {
            fixed (int* pCount = &count)
            fixed (VkQueueFamilyProperties* pFamilies = families)
            {
                device.PInvoke.vkGetPhysicalDeviceQueueFamilyProperties.Invoke(device.Handle, pCount, pFamilies);
            }
        }

        /// <inheritdoc/>
        public VkResult GetSurfaceSupport(int family, out bool supported)
        {
            Bool32 value = default;
            VkResult result = device.PInvoke.vkGetPhysicalDeviceSurfaceSupportKHR.Invoke(device.Handle, family, surface, &value);
            supported = value;
            return result;
        }
    }
}
