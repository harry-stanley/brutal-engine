using System.Collections.Generic;
using Brutal.VulkanApi;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Vulkan.Devices;
using BrutalEngine.Graphics.Vulkan.Instances;
using BrutalEngine.Graphics.Vulkan.Tests.Validation;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Devices;

/// <summary>
/// Debug names reaching the validation layer, proven by a name the layer rejects. The rejected handle is one the layer
/// doesn't track, so the class runs in the <see cref="UntrackedHandleValidation"/> collection, with no other test in
/// parallel.
/// </summary>
[Collection(UntrackedHandleValidation.Name)]
public sealed class WrongObjectTypeNamingTests
{
    [Fact]
    [Trait("Category", "Gpu")]
    public void RealDevice_WrongObjectType_ReachesValidation()
    {
        // Proves names reach the driver's layers: a handle named as the wrong type is a validation error.
        ValidationLog sink = new();
        (Logger logger, LogCategory category) = ValidationLog.Create(sink);
        using (logger)
        {
            using VulkanInstance instance = GpuDevices.CreateInstance(category, []);
            List<GpuDevices.Adapter> adapters = GpuDevices.Suitable(instance);
            Assert.NotEmpty(adapters);
            foreach (GpuDevices.Adapter adapter in adapters)
            {
                using VulkanDeviceCore core = GpuDevices.Create(instance, adapter, new VulkanCallGate(), null).Value;
                long before = instance.ValidationErrors;

                DebugNames.Set(core, VkObjectType.Image, (ulong)core.Device.Handle.VkHandle, "not an image"u8);

                Assert.True(instance.ValidationErrors > before, adapter.Candidate.Name);
            }
        }
    }
}
