using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Brutal.SdlApi;
using Brutal.VulkanApi;

namespace HelloWindow;

/// <summary>The Vulkan instance, validation messenger, window surface, device and queue, in creation order.</summary>
/// <remarks>Dispose only after the device is idle (<see cref="WaitIdle"/>): it destroys the device.</remarks>
internal sealed unsafe class VulkanContext : IDisposable
{
    private const string ValidationLayer = "VK_LAYER_KHRONOS_validation";

    private readonly Instance _instance;
    private VkDebugUtilsMessengerEXT _messenger;
    private Device? _device;
    private PhysicalDevice? _physicalDevice;
    private Queue? _queue;
    private bool _disposed;

    private VulkanContext(Instance instance) => _instance = instance;

    /// <summary>Gets the window surface.</summary>
    public VkSurfaceKHR Surface { get; private set; }

    /// <summary>Gets the chosen physical device.</summary>
    public PhysicalDevice PhysicalDevice => _physicalDevice ?? throw new InvalidOperationException("No device yet.");

    /// <summary>Gets the logical device.</summary>
    public Device Device => _device ?? throw new InvalidOperationException("No device yet.");

    /// <summary>Gets the queue used for both graphics and present.</summary>
    public Queue Queue => _queue ?? throw new InvalidOperationException("No device yet.");

    /// <summary>Gets the family of <see cref="Queue"/>.</summary>
    public int QueueFamily { get; private set; }

    /// <summary>Creates everything up to the queue. Destroys what it created if a step fails.</summary>
    /// <param name="window">The SDL window, created with <see cref="SdlWindowFlags.Vulkan"/>.</param>
    /// <param name="options">The command-line options (validation and device requirements).</param>
    /// <returns>The context.</returns>
    /// <exception cref="SampleFailureException">The validation layer or headless-surface extension is missing, or no
    /// device is suitable.</exception>
    /// <exception cref="VkResultError">A Vulkan call failed.</exception>
    /// <exception cref="SdlException">SDL couldn't create the surface.</exception>
    public static VulkanContext Create(SdlWindow window, HelloWindowOptions options)
    {
        var context = new VulkanContext(CreateInstance(options));
        try
        {
            if (options.Validation)
            {
                context._messenger = context._instance.CreateDebugUtilsMessengerEXT(ValidationMessenger.CreateInfo(), null);
            }

            context.Surface = new VkSurfaceKHR(Sdl.Vulkan.CreateSurface(window, context._instance.VkHandle));
            context.CreateDevice(options);
            return context;
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    /// <summary>Waits for the device to finish all work. Returns the status instead of throwing, so teardown can
    /// continue after a lost device (destroying objects is still valid then).</summary>
    /// <returns>The result of <c>vkDeviceWaitIdle</c>, or success if there is no device.</returns>
    public VkResult WaitIdle() => _device is null ? VkResult.Success : _device.PInvoke.vkDeviceWaitIdle.Invoke(_device.Handle);

    /// <inheritdoc/>
    public void Dispose()
    {
        // Idempotent: a second Instance.Dispose would destroy the instance and free its function table twice.
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Reverse creation order. The surface and messenger are destroyed only if they were created.
        _device?.Dispose();
        _device = null;
        if (!Surface.IsNull())
        {
            _instance.DestroySurfaceKHR(Surface, null);
            Surface = default;
        }

        if (!_messenger.IsNull())
        {
            _instance.DestroyDebugUtilsMessengerEXT(_messenger, null);
            _messenger = default;
        }

        _instance.Dispose();
    }

    private static Instance CreateInstance(HelloWindowOptions options)
    {
        bool validation = options.Validation;
        HashSet<string> available = InstanceExtensionNames();

        // SDL's offscreen driver needs VK_EXT_headless_surface. The Windows drivers checked so far (NVIDIA, Mesa
        // lavapipe) don't offer it, and SDL would only fail later with a less specific message.
        if (options.Headless && !available.Contains(VK.EXT_HEADLESS_SURFACE_EXTENSION_NAME))
        {
            throw new SampleFailureException($"--headless needs {VK.EXT_HEADLESS_SURFACE_EXTENSION_NAME}, which no installed Vulkan driver offers.");
        }

        HashSet<string> extensions = [.. Sdl.Vulkan.GetInstanceExtensions()];
        HashSet<string> layers = [];
        VkInstanceCreateFlags flags = VkInstanceCreateFlags.None;

        // MoltenVK is a portability driver: the loader lists it only if the instance opts in (docs/03-architecture.md 3.8).
        if (available.Contains(VK.KHR_PORTABILITY_ENUMERATION_EXTENSION_NAME))
        {
            extensions.Add(VK.KHR_PORTABILITY_ENUMERATION_EXTENSION_NAME);
            flags |= VkInstanceCreateFlags.EnumeratePortabilityBitKHR;
        }

        // Chained into instance creation too, so vkCreateInstance and vkDestroyInstance are validated and counted.
        // Sound: the chain points at these stack locals, which live until Vulkan.CreateInstance below has returned,
        // and the loader copies what it keeps (the messenger callback is a static function pointer).
        VkDebugUtilsMessengerCreateInfoEXT messengerInfo = ValidationMessenger.CreateInfo();
        VkValidationFeatureEnableEXT synchronizationValidation = VkValidationFeatureEnableEXT.SynchronizationValidationEXT;
        VkValidationFeaturesEXT validationFeatures = new()
        {
            Next = &messengerInfo,
            EnabledValidationFeatureCount = 1,
            EnabledValidationFeatures = &synchronizationValidation,
        };
        if (validation)
        {
            if (!HasValidationLayer())
            {
                throw new SampleFailureException($"{ValidationLayer} is not available. Install the Vulkan validation layers.");
            }

            layers.Add(ValidationLayer);
            extensions.Add(VK.EXT_DEBUG_UTILS_EXTENSION_NAME);
            extensions.Add(VK.EXT_VALIDATION_FEATURES_EXTENSION_NAME);
        }

        SampleConsole.Info($"Instance extensions: {string.Join(", ", extensions)}; layers: {(validation ? ValidationLayer : "(none)")}");
        return Vulkan.CreateInstance(new Instance.CreateInfo
        {
            AppInfo = new VkApplicationInfo { ApiVersion = VK.API_VERSION_1_3 },
            Flags = flags,
            EnabledExtensions = extensions,
            EnabledLayers = layers,
            Next = validation ? &validationFeatures : null, // a stack local that outlives this call (see above)
        });
    }

    private static bool HasValidationLayer()
    {
        var layers = new VkLayerProperties[Vulkan.EnumerateInstanceLayerProperties_Count()];
        NativeCheck.Vk(Vulkan.EnumerateInstanceLayerProperties_Write(layers), "vkEnumerateInstanceLayerProperties");
        for (int i = 0; i < layers.Length; i++)
        {
            fixed (byte* name = layers[i].LayerName)
            {
                if (ReadName(name) == ValidationLayer)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static HashSet<string> InstanceExtensionNames()
    {
        // No layer name: the extensions of the loader, the drivers and the implicit layers.
        var properties = new VkExtensionProperties[Vulkan.EnumerateInstanceExtensionProperties_Count(default)];
        NativeCheck.Vk(Vulkan.EnumerateInstanceExtensionProperties_Write(default, properties), "vkEnumerateInstanceExtensionProperties");
        HashSet<string> names = [];
        for (int i = 0; i < properties.Length; i++)
        {
            fixed (byte* name = properties[i].ExtensionName)
            {
                names.Add(ReadName(name));
            }
        }

        return names;
    }

    private void CreateDevice(HelloWindowOptions options)
    {
        PhysicalDevice[] devices = _instance.PhysicalDevices;
        var candidates = new DeviceCandidate[devices.Length];
        for (int i = 0; i < devices.Length; i++)
        {
            candidates[i] = Describe(devices[i]);
            SampleConsole.Info($"Device {i}: {candidates[i].Name} ({candidates[i].Type}), Vulkan {DeviceSelector.FormatVersion(candidates[i].ApiVersion)}, driver {candidates[i].DriverId}");
        }

        DeviceSelection selection = DeviceSelector.Select(candidates, options.RequireCpu, options.RequireDriver);
        foreach (string rejection in selection.Rejections)
        {
            SampleConsole.Info($"  rejected {rejection}");
        }

        if (!selection.Found)
        {
            throw new SampleFailureException(selection.FailureMessage);
        }

        _physicalDevice = devices[selection.ChosenIndex];
        QueueFamily = candidates[selection.ChosenIndex].GraphicsPresentQueueFamily;

        // A portability driver that exposes VK_KHR_portability_subset requires it enabled (VUID-VkDeviceCreateInfo-pProperties-04451).
        HashSet<string> extensions = [VK.KHR_SWAPCHAIN_EXTENSION_NAME];
        if (_physicalDevice.SupportsExtension(VK.KHR_PORTABILITY_SUBSET_EXTENSION_NAME))
        {
            extensions.Add(VK.KHR_PORTABILITY_SUBSET_EXTENSION_NAME);
        }

        SampleConsole.Info($"Using device {selection.ChosenIndex}, queue family {QueueFamily}, extensions {string.Join(", ", extensions)}");
        // Sound: &priority and &enabled point at stack locals that live until CreateDevice has returned, and Vulkan
        // doesn't keep the pointers.
        float priority = 1.0f;
        VkPhysicalDeviceVulkan13Features enabled = new() { DynamicRendering = true, Synchronization2 = true };
        _device = _physicalDevice.CreateDevice(new Device.CreateInfo
        {
            Extensions = extensions,
            QueueCreateInfos = [new VkDeviceQueueCreateInfo { QueueFamilyIndex = QueueFamily, QueueCount = 1, QueuePriorities = &priority }],
            Next = &enabled,
        });
        _queue = _device.GetQueue(QueueFamily, 0);
    }

    private DeviceCandidate Describe(PhysicalDevice device)
    {
        VkPhysicalDeviceProperties properties = device.GetProperties();
        VkPhysicalDeviceDriverProperties driver = new();
        VkPhysicalDeviceVulkan13Features features = new();

        // Vulkan 1.3 structs may only be chained for a device that supports 1.3; older devices are rejected anyway.
        bool isVulkan13 = properties.ApiVersion >= VK.API_VERSION_1_3;
        if (isVulkan13)
        {
            // Sound: the chains point at driver and features, stack locals of this method that outlive both queries,
            // which fill them in place.
            VkPhysicalDeviceProperties2 properties2 = new() { Next = &driver };
            device.GetProperties2(ref properties2);
            VkPhysicalDeviceFeatures2 features2 = new() { Next = &features };
            device.GetFeatures2(ref features2);
        }

        var families = new VkQueueFamilyProperties[device.GetQueueFamilyProperties_Count()];
        device.GetQueueFamilyProperties_Write(families);
        int graphicsPresentFamily = -1;
        for (int i = 0; i < families.Length && graphicsPresentFamily < 0; i++)
        {
            if ((families[i].QueueFlags & VkQueueFlags.GraphicsBit) != 0 && device.GetSurfaceSupportKHR(i, Surface))
            {
                graphicsPresentFamily = i;
            }
        }

        return new DeviceCandidate(
            ReadName(properties.DeviceName),
            properties.DeviceType,
            properties.ApiVersion,
            driver.DriverID,
            device.SupportsExtension(VK.KHR_SWAPCHAIN_EXTENSION_NAME),
            isVulkan13 && features.DynamicRendering,
            isVulkan13 && features.Synchronization2,
            graphicsPresentFamily);
    }

    // Vulkan name fields are NUL-terminated fixed-size UTF-8 arrays (the spec guarantees the terminator).
    private static string ReadName(byte* name) => Marshal.PtrToStringUTF8((nint)name) ?? string.Empty;
}
