using System;
using Brutal.VulkanApi;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Vulkan.Devices;
using BrutalEngine.Graphics.Vulkan.Instances;

namespace BrutalEngine.Graphics.Vulkan.Swapchains;

/// <summary>
/// Reads what a surface reports for a device's adapter into <see cref="SurfaceFacts"/>, the swapchain planner's input
/// (docs/design/graphics-vulkan.md section 4): its capabilities, formats and present modes, through the raw instance
/// function table.
/// </summary>
/// <remarks>
/// <para>
/// Formats and present modes use the two-call pattern with the bounded <c>VK_INCOMPLETE</c> retry of the instance code
/// (<see cref="InstanceExtensions.MaxEnumerationAttempts"/> attempts, then the last <c>VK_INCOMPLETE</c> fails through
/// the gate as <see cref="ErrorKind.Native"/>), because BRUTAL's <c>_Count</c> and <c>_Write</c> helpers hide the
/// written count and drop the result (upstream issue draft #18). Only the entries the fill wrote are returned. Every
/// call passes through the gate, so the fault injector reaches it.
/// </para>
/// <para>
/// Creation-time only: allocates the arrays it returns. Not thread-safe: the device's owner thread calls it.
/// </para>
/// </remarks>
internal static class SurfaceQueries
{
    /// <summary>Reads a surface's facts for the adapter of a live device.</summary>
    /// <param name="core">The device; its adapter is asked.</param>
    /// <param name="surface">The surface, created from the device's instance; not the null handle.</param>
    /// <param name="gate">The device's gate.</param>
    /// <returns>The facts, or the first failed call's error (<see cref="VkCheck.Error"/>).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="core"/> or <paramref name="gate"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="surface"/> is the null handle.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="core"/> is disposed.</exception>
    public static Result<SurfaceFacts> Read(VulkanDeviceCore core, VkSurfaceKHR surface, VulkanCallGate gate)
    {
        ArgumentNullException.ThrowIfNull(core);
        if (surface.IsNull())
        {
            throw new ArgumentException("The surface is the null handle.", nameof(surface));
        }

        return Read(new AdapterSource(core.Device.PhysicalDevice, surface), gate);
    }

    /// <summary><see cref="Read(VulkanDeviceCore, VkSurfaceKHR, VulkanCallGate)"/> over given surface queries.</summary>
    /// <typeparam name="TSource">The surface queries; the real ones are a struct, so nothing is boxed.</typeparam>
    /// <param name="source">The surface queries.</param>
    /// <param name="gate">The gate every call passes through.</param>
    /// <returns>As the public overload.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="gate"/> is <see langword="null"/>.</exception>
    internal static Result<SurfaceFacts> Read<TSource>(TSource source, VulkanCallGate gate)
        where TSource : ISource
    {
        ArgumentNullException.ThrowIfNull(gate);
        VkSurfaceCapabilitiesKHR capabilities = default;
        VkResult result = gate.TryTakeFault(VulkanCall.GetSurfaceCapabilities, out VkResult injected) ? injected : source.GetCapabilities(out capabilities);
        Result checkedResult = gate.Check(result, VulkanCall.GetSurfaceCapabilities);
        if (checkedResult.IsFailure)
        {
            return checkedResult.Error;
        }

        Result<VkSurfaceFormatKHR[]> formats = Enumerate<FormatQuery<TSource>, VkSurfaceFormatKHR>(new(source), VulkanCall.GetSurfaceFormats, gate);
        if (!formats.TryGetValue(out VkSurfaceFormatKHR[]? formatList))
        {
            return formats.Error;
        }

        Result<VkPresentModeKHR[]> modes = Enumerate<ModeQuery<TSource>, VkPresentModeKHR>(new(source), VulkanCall.GetSurfacePresentModes, gate);
        if (!modes.TryGetValue(out VkPresentModeKHR[]? modeList))
        {
            return modes.Error;
        }

        return new SurfaceFacts
        {
            CurrentExtent = capabilities.CurrentExtent,
            MinExtent = capabilities.MinImageExtent,
            MaxExtent = capabilities.MaxImageExtent,
            MinImageCount = capabilities.MinImageCount,
            MaxImageCount = capabilities.MaxImageCount,
            CurrentTransform = capabilities.CurrentTransform,
            SupportedCompositeAlpha = capabilities.SupportedCompositeAlpha,
            SupportedUsage = capabilities.SupportedUsageFlags,
            Formats = formatList,
            PresentModes = modeList,
        };
    }

    // The two-call pattern with the bounded retry; only the entries the fill wrote come back.
    private static Result<T[]> Enumerate<TQuery, T>(TQuery query, VulkanCall call, VulkanCallGate gate)
        where TQuery : IQuery<T>
        where T : unmanaged
    {
        VkResult result = VkResult.Incomplete;
        for (int attempt = 0; attempt < InstanceExtensions.MaxEnumerationAttempts && result == VkResult.Incomplete; attempt++)
        {
            int count = 0;
            result = gate.TryTakeFault(call, out VkResult injected) ? injected : query.Call(ref count, []);
            if (result != VkResult.Success || count == 0)
            {
                break;
            }

            var items = new T[count];
            result = gate.TryTakeFault(call, out injected) ? injected : query.Call(ref count, items);
            if (result == VkResult.Success)
            {
                return count < items.Length ? items[..count] : items;
            }
        }

        Result checkedResult = gate.Check(result, call);
        return checkedResult.TryGetError(out EngineError error) ? error : Array.Empty<T>();
    }

    /// <summary>
    /// The queries a surface answers for one adapter, so tests can script a surface. They mirror their Vulkan commands;
    /// the enumerations take an empty span to ask for the count and a non-empty one to be filled with up to
    /// <c>count</c> entries.
    /// </summary>
    internal interface ISource
    {
        /// <summary><c>vkGetPhysicalDeviceSurfaceCapabilitiesKHR</c>.</summary>
        /// <param name="capabilities">The capabilities.</param>
        /// <returns>The command's result.</returns>
        public VkResult GetCapabilities(out VkSurfaceCapabilitiesKHR capabilities);

        /// <summary><c>vkGetPhysicalDeviceSurfaceFormatsKHR</c>.</summary>
        /// <param name="count">In: the span's capacity (ignored for an empty span). Out: the count, or how many were written.</param>
        /// <param name="formats">Empty to ask for the count; otherwise the entries to fill.</param>
        /// <returns>The command's result.</returns>
        public VkResult GetFormats(ref int count, Span<VkSurfaceFormatKHR> formats);

        /// <summary><c>vkGetPhysicalDeviceSurfacePresentModesKHR</c>.</summary>
        /// <param name="count">As <see cref="GetFormats"/>.</param>
        /// <param name="modes">As <see cref="GetFormats"/>.</param>
        /// <returns>The command's result.</returns>
        public VkResult GetPresentModes(ref int count, Span<VkPresentModeKHR> modes);
    }

    // One enumeration of a source, so the retry loop is written once for both element types.
    private interface IQuery<T>
        where T : unmanaged
    {
        public VkResult Call(ref int count, Span<T> items);
    }

    private readonly struct FormatQuery<TSource>(TSource source) : IQuery<VkSurfaceFormatKHR>
        where TSource : ISource
    {
        public VkResult Call(ref int count, Span<VkSurfaceFormatKHR> items) => source.GetFormats(ref count, items);
    }

    private readonly struct ModeQuery<TSource>(TSource source) : IQuery<VkPresentModeKHR>
        where TSource : ISource
    {
        public VkResult Call(ref int count, Span<VkPresentModeKHR> items) => source.GetPresentModes(ref count, items);
    }

    /// <summary>
    /// The real queries, through the instance's raw function table (<c>Brutal.Vulkan/Generated/Handles/VkInstance.gen.cs:72-74</c>),
    /// which BRUTAL's <c>PhysicalDevice.PInvoke</c> shares.
    /// </summary>
    /// <param name="adapter">The device's adapter; its instance outlives this value.</param>
    /// <param name="surface">The surface, from the same instance; <c>VK_KHR_surface</c> is enabled with it.</param>
    private readonly unsafe struct AdapterSource(PhysicalDevice adapter, VkSurfaceKHR surface) : ISource
    {
        // Sound, for every member: the pointers are to an out parameter or to spans pinned here, which outlive the call,
        // and the counts say how many entries the spans hold (an empty span pins as null, the count query). The
        // entry points are loaded because a surface exists, which needs VK_KHR_surface.
        public VkResult GetCapabilities(out VkSurfaceCapabilitiesKHR capabilities)
        {
            fixed (VkSurfaceCapabilitiesKHR* pCapabilities = &capabilities)
            {
                *pCapabilities = default;
                return adapter.PInvoke.vkGetPhysicalDeviceSurfaceCapabilitiesKHR.Invoke(adapter.Handle, surface, pCapabilities);
            }
        }

        public VkResult GetFormats(ref int count, Span<VkSurfaceFormatKHR> formats)
        {
            fixed (int* pCount = &count)
            fixed (VkSurfaceFormatKHR* pFormats = formats)
            {
                return adapter.PInvoke.vkGetPhysicalDeviceSurfaceFormatsKHR.Invoke(adapter.Handle, surface, pCount, pFormats);
            }
        }

        public VkResult GetPresentModes(ref int count, Span<VkPresentModeKHR> modes)
        {
            fixed (int* pCount = &count)
            fixed (VkPresentModeKHR* pModes = modes)
            {
                return adapter.PInvoke.vkGetPhysicalDeviceSurfacePresentModesKHR.Invoke(adapter.Handle, surface, pCount, pModes);
            }
        }
    }
}
