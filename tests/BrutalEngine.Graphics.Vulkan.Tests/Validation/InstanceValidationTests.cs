using System;
using Brutal.VulkanApi;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Vulkan.Instances;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Validation;

/// <summary>The instance factory's validation chain, messenger and failure paths.</summary>
public sealed class InstanceValidationTests
{
    [Fact]
    [Trait("Category", "Gpu")]
    public void SynchronizationValidation_Hazard_IsReported()
    {
        ValidationLog log = new();
        (Logger logger, LogCategory category) = ValidationLog.Create(log);
        using (logger)
        {
            using VulkanInstance instance = GpuValidation.Create(category);

            GpuValidation.RecordWriteAfterWrite(instance);

            // Only the chained VkValidationFeaturesEXT enables synchronization validation; without it this is clean.
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("SYNC-HAZARD-WRITE-AFTER-WRITE", StringComparison.Ordinal));
            Assert.True(instance.ValidationErrors > 0);
        }
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void Create_WithoutValidation_HasNoSink()
    {
        GpuValidation.BindLoader();
        using VulkanInstance instance = InstanceFactory.Create(new InstanceFactory.Options(), new VulkanCallGate()).Value;

        Assert.Null(instance.Sink);
        Assert.Equal((0, 0, 0), (instance.ValidationErrors, instance.ValidationWarnings, instance.ValidationAllowed));
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void CreateMessenger_Fails_DisposesAndReleases()
    {
        GpuValidation.BindLoader();
        ValidationSink.HandleTracker tracker = new();
        InstanceFactory.DestroyTracker destroys = new();
        VulkanFaultInjector faults = new();
        faults.FailNth(VulkanCall.CreateDebugUtilsMessenger, 1, VkResult.ErrorOutOfHostMemory);
        VulkanCallGate gate = new() { Faults = faults };

        Result<VulkanInstance> result = InstanceFactory.Create(new InstanceFactory.Options { Validation = true, ValidationHandles = tracker, InstanceDestroys = destroys }, gate);

        if (result.TryGetValue(out VulkanInstance? created))
        {
            created.Dispose();
        }

        Assert.Equal(new EngineError(ErrorKind.OutOfMemory, "vkCreateDebugUtilsMessengerEXT failed", (int)VkResult.ErrorOutOfHostMemory), result.Error);
        Assert.False(faults.TryTake(VulkanCall.CreateDebugUtilsMessenger, out _));
        Assert.Equal(0, tracker.Live);
        Assert.Equal(1, destroys.Destroyed);
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void Create_InstanceCreationFails_Releases()
    {
        GpuValidation.BindLoader();
        ValidationSink.HandleTracker tracker = new();
        InstanceExtensions.Selection selection = new(["VK_BE_not_an_extension", VK.EXT_DEBUG_UTILS_EXTENSION_NAME], [], VkInstanceCreateFlags.None, Validation: true);

        Result<VulkanInstance> result = InstanceFactory.Create(selection, new InstanceFactory.Options { ValidationHandles = tracker }, new VulkanCallGate());

        if (result.TryGetValue(out VulkanInstance? created))
        {
            created.Dispose();
        }

        Assert.Equal((int)VkResult.ErrorExtensionNotPresent, result.Error.NativeCode);
        Assert.Equal(0, tracker.Live);
    }

    [Fact]
    public void Create_AllowanceWithoutReason_ThrowsBeforeNativeCalls()
    {
        VulkanFaultInjector faults = new();
        faults.FailNth(VulkanCall.CreateDebugUtilsMessenger, 1, VkResult.ErrorOutOfHostMemory);
        InstanceExtensions.Selection selection = new([VK.EXT_DEBUG_UTILS_EXTENSION_NAME], [], VkInstanceCreateFlags.None, Validation: true);
        InstanceFactory.Options options = new() { ValidationAllowList = new[] { new ValidationAllowance(1, " ") } };

        _ = Assert.Throws<ArgumentException>("allowList", () => InstanceFactory.Create(selection, options, new VulkanCallGate { Faults = faults }));

        // The messenger fault is still armed: nothing past the sink ran.
        Assert.True(faults.TryTake(VulkanCall.CreateDebugUtilsMessenger, out _));
    }

    [Theory]
    [InlineData("Vulkan call to vkCreateInstance failed with error: VK_ERROR_EXTENSION_NOT_PRESENT", true)]
    [InlineData("Vulkan call to vkCreateInstance failed with error: VK_ERROR_INITIALIZATION_FAILED", true)]
    [InlineData("Vulkan call to vkEnumeratePhysicalDevices failed with error: VK_ERROR_INITIALIZATION_FAILED", false)]
    [InlineData("Vulkan call to vkCreateInstanceX failed", false)]
    [InlineData("", false)]
    public void IsCreateInstanceFailure_OnlyForVkCreateInstance(string message, bool expected)
    {
        // BRUTAL's messages, from Brutal.Vulkan/Generated/PInvoke/VulkanApi/VulkanApiEx.gen.cs:33-60 and
        // Brutal.Vulkan/Generated/Extensions/VkInstanceExtensions.gen.cs:64-74.
        Assert.Equal(expected, InstanceFactory.IsCreateInstanceFailure(new VkResultError(VkResult.ErrorInitializationFailed, message)));
    }
}
