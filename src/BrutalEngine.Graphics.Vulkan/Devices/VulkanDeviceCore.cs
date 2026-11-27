using System;
using System.Collections.Generic;
using Brutal.VulkanApi;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Vulkan.Instances;

namespace BrutalEngine.Graphics.Vulkan.Devices;

/// <summary>
/// A logical device and its one queue (docs/design/graphics-vulkan.md section 3, "Queues" and "Device extensions and the
/// feature chain"), created on a chosen adapter through BRUTAL's <c>PhysicalDevice.CreateDevice</c>, the only way to get
/// BRUTAL's device function table.
/// </summary>
/// <remarks>
/// <para>
/// One queue (priority 1.0) from the chosen family serves graphics, transfer, present and, when the family supports it,
/// compute. It is fetched once at creation through BRUTAL's <c>Device.GetQueue</c>, and only its raw handle is kept, so
/// nothing reaches BRUTAL's locking <c>Queue</c> members (section 13, rule 6).
/// </para>
/// <para>
/// BRUTAL's wrapper throws <see cref="VkResultError"/>, so this is one of the three call sites that catch it and convert
/// it with <see cref="VkCheck.FromException"/> (section 13, rule 2). The gate's fault check runs before the wrapper, so
/// an injected fault creates nothing.
/// </para>
/// <para>
/// <see cref="Dispose"/> is idempotent through this class's own flag, because BRUTAL's <c>Device.Dispose</c> destroys the
/// device and frees its function table again on every call (<c>Brutal.Vulkan/Manual/Classes/Device.cs:120-126</c>;
/// upstream issue draft #20). The owner disposes it before the instance, after the device is idle and everything created
/// from it is destroyed.
/// </para>
/// <para>
/// Not thread-safe: the device's owner thread creates, uses and disposes it. Creation allocates (BRUTAL's wrapper, the
/// extension set and this object); the properties of a live device allocate nothing.
/// </para>
/// </remarks>
internal sealed class VulkanDeviceCore : IDisposable
{
    /// <summary>The wrapped command's name, for the error BRUTAL's exception becomes.</summary>
    internal const string CreateDeviceName = "vkCreateDevice";

    private readonly Device _device;
    private readonly InstanceFactory.DestroyTracker? _destroys;
    private bool _disposed;

    private VulkanDeviceCore(Device device, VkQueue queue, int queueFamily, FeatureChain.NegotiatedFeatures features, DeviceExtensionSet.Selection extensions, InstanceFactory.DestroyTracker? destroys)
    {
        _device = device;
        Queue = queue;
        QueueFamily = queueFamily;
        Features = features;
        Extensions = extensions;
        _destroys = destroys;
    }

    /// <summary>Gets BRUTAL's wrapper, for its function table.</summary>
    /// <exception cref="ObjectDisposedException">The device is disposed.</exception>
    public Device Device
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _device;
        }
    }

    /// <summary>Gets the raw handle of the device's one queue; never the null handle. Stale once disposed.</summary>
    public VkQueue Queue { get; }

    /// <summary>Gets the Vulkan index of the queue family the queue belongs to.</summary>
    public int QueueFamily { get; }

    /// <summary>Gets the enabled features.</summary>
    public FeatureChain.NegotiatedFeatures Features { get; }

    /// <summary>Gets the enabled extensions.</summary>
    public DeviceExtensionSet.Selection Extensions { get; }

    /// <summary>Gets whether <see cref="Dispose"/> has run.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>Creates a device on an adapter of a live instance.</summary>
    /// <param name="instance">The instance the adapter was enumerated from; it outlives the device.</param>
    /// <param name="adapter">The adapter, one of <paramref name="instance"/>'s physical devices.</param>
    /// <param name="queueFamily">The Vulkan index of the queue family to take the queue from (<c>QueueFamilyChoice.Choose</c>).</param>
    /// <param name="features">The negotiated features (<see cref="FeatureChain.Negotiate"/>).</param>
    /// <param name="extensions">The chosen extensions (<see cref="DeviceExtensionSet.Choose"/>).</param>
    /// <param name="gate">The device's gate.</param>
    /// <param name="destroys">A test's counter of destroyed devices, or <see langword="null"/>.</param>
    /// <returns>
    /// The device, owned by the caller; or the injected fault, or BRUTAL's <see cref="VkResultError"/> as an error with
    /// <see cref="VkCheck.KindOf"/>'s kind and the code. Nothing is left to destroy after a failure.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="instance"/>, <paramref name="adapter"/>, <paramref name="gate"/> or the extension list is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="adapter"/> belongs to another instance.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="queueFamily"/> is negative.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="instance"/> is disposed.</exception>
    public static Result<VulkanDeviceCore> Create(
        VulkanInstance instance,
        PhysicalDevice adapter,
        int queueFamily,
        in FeatureChain.NegotiatedFeatures features,
        in DeviceExtensionSet.Selection extensions,
        VulkanCallGate gate,
        InstanceFactory.DestroyTracker? destroys = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(adapter);

        // Its own check, so a disposed instance is refused even if the ownership check below changes.
        ObjectDisposedException.ThrowIf(instance.IsDisposed, instance);
        if (!ReferenceEquals(adapter.Instance, instance.Brutal))
        {
            throw new ArgumentException("The adapter belongs to another instance.", nameof(adapter));
        }

        return Create(new AdapterFactory(adapter), queueFamily, features, extensions, gate, destroys);
    }

    /// <summary><see cref="Create(VulkanInstance, PhysicalDevice, int, in FeatureChain.NegotiatedFeatures, in DeviceExtensionSet.Selection, VulkanCallGate, InstanceFactory.DestroyTracker?)"/> through a given factory.</summary>
    /// <typeparam name="TFactory">Makes BRUTAL's call; the real one is a struct, so nothing is boxed.</typeparam>
    /// <param name="factory">Makes BRUTAL's call.</param>
    /// <param name="queueFamily">The Vulkan index of the queue family.</param>
    /// <param name="features">The negotiated features.</param>
    /// <param name="extensions">The chosen extensions.</param>
    /// <param name="gate">The device's gate.</param>
    /// <param name="destroys">A test's counter of destroyed devices, or <see langword="null"/>.</param>
    /// <returns>As the public overload.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="gate"/> or the extension list is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="queueFamily"/> is negative.</exception>
    internal static unsafe Result<VulkanDeviceCore> Create<TFactory>(
        TFactory factory,
        int queueFamily,
        in FeatureChain.NegotiatedFeatures features,
        in DeviceExtensionSet.Selection extensions,
        VulkanCallGate gate,
        InstanceFactory.DestroyTracker? destroys)
        where TFactory : IFactory
    {
        ArgumentOutOfRangeException.ThrowIfNegative(queueFamily);
        if (extensions.Extensions is null)
        {
            throw new ArgumentNullException(nameof(extensions), "The selection has no extension list.");
        }

        ArgumentNullException.ThrowIfNull(gate);
        if (gate.TryTakeFault(VulkanCall.CreateDevice, out VkResult injected))
        {
            return gate.Check(injected, VulkanCall.CreateDevice).Error;
        }

        float priority = 1.0f;
        FeatureChain.Storage chain;
        VkPhysicalDeviceFeatures2* head = FeatureChain.Fill(&chain, features, extensions.SwapchainMaintenance1 is not null);
        VkDeviceQueueCreateInfo queue = new() { QueueFamilyIndex = queueFamily, QueueCount = 1, QueuePriorities = &priority };
        Device device;
        try
        {
            // Sound: the priority, the chain and the queue description are stack locals of this method, so every pointer
            // in the description stays valid until BRUTAL returns; vkCreateDevice copies what it keeps. pEnabledFeatures
            // stays null, as the specification requires with VkPhysicalDeviceFeatures2 chained.
            device = factory.CreateDevice(new Device.CreateInfo
            {
                Extensions = new HashSet<string>(extensions.Extensions, StringComparer.Ordinal),
                QueueCreateInfos = new ReadOnlySpan<VkDeviceQueueCreateInfo>(&queue, 1),
                Next = head,
            });
        }
        catch (VkResultError error)
        {
            return VkCheck.FromException(error, CreateDeviceName);
        }

        return new VulkanDeviceCore(device, device.GetQueue(queueFamily, 0).Handle, queueFamily, features, extensions, destroys);
    }

    /// <summary>Destroys the device once; later calls do nothing.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Set first, so a throwing destroy is never retried against a freed function table.
        _disposed = true;
        _device.Dispose();
        _destroys?.OnDestroyed();
    }

    /// <summary>Makes BRUTAL's device creation, so tests can observe it or make it throw.</summary>
    internal interface IFactory
    {
        /// <summary>Creates the device, as <c>PhysicalDevice.CreateDevice</c> does (<c>Brutal.Vulkan/Manual/Classes/PhysicalDevice.cs:200</c>).</summary>
        /// <param name="info">The creation description; its pointers are valid only during the call.</param>
        /// <returns>The created device.</returns>
        /// <exception cref="VkResultError"><c>vkCreateDevice</c> failed.</exception>
        public Device CreateDevice(Device.CreateInfo info);
    }

    /// <summary>The real factory: BRUTAL's wrapper on an adapter.</summary>
    /// <param name="adapter">The adapter; its instance outlives the device.</param>
    internal readonly struct AdapterFactory(PhysicalDevice adapter) : IFactory
    {
        /// <inheritdoc/>
        public Device CreateDevice(Device.CreateInfo info) => adapter.CreateDevice(info);
    }
}
