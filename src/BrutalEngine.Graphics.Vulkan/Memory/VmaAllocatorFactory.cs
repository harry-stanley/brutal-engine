using System;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Vulkan.Devices;

namespace BrutalEngine.Graphics.Vulkan.Memory;

/// <summary>
/// Creates a device's VMA allocator (docs/design/graphics-vulkan.md section 6, "Only the raw, result-returning calls")
/// through the raw <c>vmaCreateAllocator</c>, because BRUTAL's <c>CreateVmaAllocator</c> throws a bare
/// <see cref="Exception"/> on failure (<c>Brutal.Vulkan.Vma/Manual/VmaDeviceExtensions.cs:38-40</c>; upstream issue
/// draft #19).
/// </summary>
/// <remarks>
/// <para>
/// The description is the one BRUTAL's helper builds (<c>Brutal.Vulkan.Vma/Manual/VmaDeviceExtensions.cs:26-35</c>):
/// the instance, adapter and device, and a <see cref="VmaVulkanFunctions"/> with only <c>vkGetInstanceProcAddr</c>
/// (BRUTAL's bound loader entry point) and the device's <c>vkGetDeviceProcAddr</c>, from which VMA fetches the rest.
/// It adds <see cref="VulkanApiVersion"/> 1.3, the version the instance asks for, and
/// <see cref="VmaAllocatorCreateFlags.ExtMemoryBudgetBit"/> exactly when the device enabled <c>VK_EXT_memory_budget</c>.
/// </para>
/// <para>
/// The call goes through the device's gate (section 13, rule 5): the fault check runs first, so an injected fault
/// creates nothing; a native failure becomes the gate's error (out of host or device memory <c>OutOfMemory</c>, a lost
/// device <c>Unavailable</c> and marked on the gate, any other code the kind section 13's table gives it, <c>Native</c>
/// by default). VMA's <c>vmaCreateAllocator</c> deletes a half-created allocator and returns the null handle (read in
/// VMA's source; no real driver failure can be provoked to observe it), so a failure leaves nothing to destroy.
/// </para>
/// <para>Stateless; called on the device's owner thread at device creation.</para>
/// </remarks>
internal static class VmaAllocatorFactory
{
    /// <summary><c>VK_API_VERSION_1_3</c>, the API version VMA is told the instance and device use.</summary>
    public const uint VulkanApiVersion = VK.API_VERSION_1_3;

    /// <summary>Creates the allocator of a live device.</summary>
    /// <param name="core">The device; it outlives the allocator.</param>
    /// <param name="gate">The device's gate.</param>
    /// <returns>The allocator, owned by the caller, who calls <see cref="VmaHandle.Destroy"/> once; or the error.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="core"/> or <paramref name="gate"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="core"/> is disposed.</exception>
    public static Result<VmaHandle> Create(VulkanDeviceCore core, VulkanCallGate gate) => Create(default(NativeVma), core, gate);

    /// <summary><see cref="Create(VulkanDeviceCore, VulkanCallGate)"/> through a given VMA entry point.</summary>
    /// <typeparam name="TNative">Makes the native call; the real one is a struct, so nothing is boxed.</typeparam>
    /// <param name="native">Makes the native call.</param>
    /// <param name="core">The device; it outlives the allocator.</param>
    /// <param name="gate">The device's gate.</param>
    /// <returns>As the public overload.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="core"/> or <paramref name="gate"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="core"/> is disposed.</exception>
    internal static Result<VmaHandle> Create<TNative>(TNative native, VulkanDeviceCore core, VulkanCallGate gate)
        where TNative : INative
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(gate);
        Device device = core.Device;
        PhysicalDevice adapter = device.PhysicalDevice;
        Request request = new(
            adapter.Instance.Handle,
            adapter.Handle,
            device.Handle,
            global::Brutal.VulkanApi.Vulkan.GetInstanceProcAddr,
            device.PInvoke.vkGetDeviceProcAddr,
            core.Extensions.MemoryBudget);
        return Create(native, request, gate);
    }

    /// <summary><see cref="Create(VulkanDeviceCore, VulkanCallGate)"/> with given handles and entry points, through a given VMA entry point.</summary>
    /// <typeparam name="TNative">Makes the native call; the real one is a struct, so nothing is boxed.</typeparam>
    /// <param name="native">Makes the native call.</param>
    /// <param name="request">The handles, entry points and enabled extensions VMA is given.</param>
    /// <param name="gate">The device's gate.</param>
    /// <returns>As the public overload.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="gate"/> is <see langword="null"/>.</exception>
    internal static unsafe Result<VmaHandle> Create<TNative>(TNative native, in Request request, VulkanCallGate gate)
        where TNative : INative
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (gate.TryTakeFault(VulkanCall.CreateVmaAllocator, out VkResult injected))
        {
            return gate.Check(injected, VulkanCall.CreateVmaAllocator).Error;
        }

        VmaVulkanFunctions functions = new()
        {
            VkGetInstanceProcAddr = request.GetInstanceProcAddr,
            VkGetDeviceProcAddr = request.GetDeviceProcAddr,
        };
        VmaAllocatorCreateInfo info = new()
        {
            Flags = request.MemoryBudget ? VmaAllocatorCreateFlags.ExtMemoryBudgetBit : VmaAllocatorCreateFlags.None,
            PhysicalDevice = request.PhysicalDevice,
            Device = request.Device,
            Instance = request.Instance,
            VulkanApiVersion = VulkanApiVersion,

            // Sound: the functions are a local of this method, valid until VMA returns; VMA copies them.
            PVulkanFunctions = &functions,
        };
        VmaAllocatorHandle handle = default;
        Result result = gate.Check(native.CreateAllocator(&info, &handle), VulkanCall.CreateVmaAllocator);
        return result.IsSuccess ? new VmaHandle(handle) : result.Error;
    }

    /// <summary>What VMA is given: the device's handles, the two entry points and whether the budget extension is on.</summary>
    /// <param name="Instance">The instance the device was created from.</param>
    /// <param name="PhysicalDevice">The adapter.</param>
    /// <param name="Device">The device.</param>
    /// <param name="GetInstanceProcAddr">The loader's <c>vkGetInstanceProcAddr</c>.</param>
    /// <param name="GetDeviceProcAddr">The device's <c>vkGetDeviceProcAddr</c>.</param>
    /// <param name="MemoryBudget">Whether the device enabled <c>VK_EXT_memory_budget</c>.</param>
    internal readonly record struct Request(
        VkInstance Instance,
        VkPhysicalDevice PhysicalDevice,
        VkDevice Device,
        VkGetInstanceProcAddr GetInstanceProcAddr,
        VkGetDeviceProcAddr GetDeviceProcAddr,
        bool MemoryBudget);

    /// <summary>Makes the native <c>vmaCreateAllocator</c> call, so tests can observe it or script its result.</summary>
    internal interface INative
    {
        /// <summary>Calls <c>vmaCreateAllocator</c> (<c>Brutal.Vulkan.Vma/Generated/Vma.gen.cs:7-11</c>).</summary>
        /// <param name="info">The description; valid only during the call.</param>
        /// <param name="allocator">Receives the allocator; VMA leaves it null on failure.</param>
        /// <returns>VMA's result.</returns>
        public unsafe VkResult CreateAllocator(VmaAllocatorCreateInfo* info, VmaAllocatorHandle* allocator);
    }

    /// <summary>The real entry point: BRUTAL's generated binding.</summary>
    internal readonly struct NativeVma : INative
    {
        /// <inheritdoc/>
        public unsafe VkResult CreateAllocator(VmaAllocatorCreateInfo* info, VmaAllocatorHandle* allocator) =>
            Vma.PInvoke.vmaCreateAllocator(info, allocator);
    }
}
