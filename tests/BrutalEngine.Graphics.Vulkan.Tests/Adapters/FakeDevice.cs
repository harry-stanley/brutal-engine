using System;
using System.Collections.Generic;
using System.Text;
using Brutal.VulkanApi;
using BrutalEngine.Graphics.Vulkan.Adapters;

namespace BrutalEngine.Graphics.Vulkan.Tests.Adapters;

/// <summary>
/// A scripted physical device for <see cref="CandidateReader"/>: answers each query from its properties, fills a
/// chained structure only when one is chained (as a driver does), and records every call.
/// </summary>
internal sealed unsafe class FakeDevice : CandidateReader.ISource
{
    // VkStructureType values from the Vulkan specification (VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_DRIVER_PROPERTIES,
    // VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_3_FEATURES).
    public const int DriverPropertiesType = 1000196000;
    public const int Vulkan13FeaturesType = 53;

    public int Major { get; init; } = 1;
    public int Minor { get; init; } = 3;
    public int Patch { get; init; } = 280;
    public string Name { get; init; } = "Fake GPU";
    public VkPhysicalDeviceType Type { get; init; } = VkPhysicalDeviceType.DiscreteGpu;
    public VkDriverId Driver { get; init; } = VkDriverId.MESALlvmpipe;
    public float TimestampPeriod { get; init; } = 1.5f;
    public bool SamplerAnisotropy { get; init; }
    public bool TextureCompressionBC { get; init; }
    public bool TextureCompressionAstcLdr { get; init; }
    public bool DynamicRendering { get; init; }
    public bool Synchronization2 { get; init; }
    public string[] Extensions { get; init; } = ["VK_KHR_swapchain"];

    /// <summary>How many extensions the successful fill writes; all of them by default.</summary>
    public int? ExtensionsWritten { get; init; }

    /// <summary>The count query's result.</summary>
    public VkResult CountResult { get; init; } = VkResult.Success;

    /// <summary>How many fill calls return <c>VK_INCOMPLETE</c> (as if an extension appeared) before one succeeds.</summary>
    public int IncompleteFills { get; set; }

    public (VkQueueFlags Flags, int TimestampBits, bool Present)[] Families { get; init; } = [(VkQueueFlags.GraphicsBit, 64, true)];

    /// <summary>How many families the fill call writes; all of them by default.</summary>
    public int? FamiliesWritten { get; init; }

    public bool HasSurface { get; init; }
    public VkResult SurfaceResult { get; init; } = VkResult.Success;

    // What was asked: the sType of each query's chained structure (0 for none), and the families asked about present.
    public List<int> PropertiesChains { get; } = [];
    public List<int> FeaturesChains { get; } = [];
    public List<int> SurfaceQueries { get; } = [];
    public int ExtensionCalls { get; private set; }

    public void GetProperties2(VkPhysicalDeviceProperties2* properties)
    {
        PropertiesChains.Add(TypeOf(properties->Next));
        properties->Properties.ApiVersion = (Major << 22) | (Minor << 12) | Patch; // VK_MAKE_API_VERSION(0, ...)
        properties->Properties.DeviceType = Type;
        properties->Properties.Limits.TimestampPeriod = TimestampPeriod;
        Span<byte> name = new(properties->Properties.DeviceName, 256);
        name.Clear();
        _ = Encoding.UTF8.GetBytes(Name, name);
        if (TypeOf(properties->Next) == DriverPropertiesType)
        {
            ((VkPhysicalDeviceDriverProperties*)properties->Next)->DriverID = Driver;
        }
    }

    public void GetFeatures2(VkPhysicalDeviceFeatures2* features)
    {
        FeaturesChains.Add(TypeOf(features->Next));
        features->Features.SamplerAnisotropy = SamplerAnisotropy;
        features->Features.TextureCompressionBC = TextureCompressionBC;
        features->Features.TextureCompressionASTCLDR = TextureCompressionAstcLdr;
        if (TypeOf(features->Next) == Vulkan13FeaturesType)
        {
            var vulkan13 = (VkPhysicalDeviceVulkan13Features*)features->Next;
            vulkan13->DynamicRendering = DynamicRendering;
            vulkan13->Synchronization2 = Synchronization2;
        }
    }

    public VkResult EnumerateExtensions(ref int count, Span<VkExtensionProperties> properties)
    {
        ExtensionCalls++;
        if (properties.IsEmpty)
        {
            count = Extensions.Length + Math.Min(IncompleteFills, 1);
            return CountResult;
        }

        int written = Math.Min(count, Extensions.Length);
        fixed (VkExtensionProperties* entries = properties)
        {
            for (int i = 0; i < written; i++)
            {
                _ = Encoding.UTF8.GetBytes(Extensions[i], new Span<byte>(entries[i].ExtensionName, 255));
            }
        }

        if (IncompleteFills > 0)
        {
            count = written;
            IncompleteFills--;
            return VkResult.Incomplete;
        }

        count = Math.Min(written, ExtensionsWritten ?? written);
        return VkResult.Success;
    }

    public void GetQueueFamilies(ref int count, Span<VkQueueFamilyProperties> families)
    {
        if (families.IsEmpty)
        {
            count = Families.Length;
            return;
        }

        count = Math.Min(count, FamiliesWritten ?? Families.Length);
        for (int i = 0; i < count; i++)
        {
            families[i] = new VkQueueFamilyProperties { QueueFlags = Families[i].Flags, QueueCount = 1, TimestampValidBits = Families[i].TimestampBits };
        }
    }

    public VkResult GetSurfaceSupport(int family, out bool supported)
    {
        SurfaceQueries.Add(family);
        supported = Families[family].Present;
        return SurfaceResult;
    }

    // Every Vulkan structure starts with its sType.
    private static int TypeOf(void* next) => next is null ? 0 : *(int*)next;
}
