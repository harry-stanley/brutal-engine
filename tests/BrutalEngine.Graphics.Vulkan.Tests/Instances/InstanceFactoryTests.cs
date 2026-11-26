using System;
using Brutal.VulkanApi;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Vulkan.Instances;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Instances;

public sealed class InstanceFactoryTests
{
    private const string Validation = "VK_LAYER_KHRONOS_validation";

    // Every result-returning call of a validated creation, in order, as the gate counts them: the loader's extensions
    // (count, fill), the layers (count, fill), the validation layer's extensions (count, fill), then the instance.
    // The call is passed as its name, because VulkanCall is internal and the theory is public.
    public static TheoryData<string, int, string> Calls => new()
    {
        { nameof(VulkanCall.EnumerateInstanceExtensionProperties), 1, "vkEnumerateInstanceExtensionProperties failed" },
        { nameof(VulkanCall.EnumerateInstanceExtensionProperties), 2, "vkEnumerateInstanceExtensionProperties failed" },
        { nameof(VulkanCall.EnumerateInstanceLayerProperties), 1, "vkEnumerateInstanceLayerProperties failed" },
        { nameof(VulkanCall.EnumerateInstanceLayerProperties), 2, "vkEnumerateInstanceLayerProperties failed" },
        { nameof(VulkanCall.EnumerateInstanceExtensionProperties), 3, "vkEnumerateInstanceExtensionProperties failed" },
        { nameof(VulkanCall.EnumerateInstanceExtensionProperties), 4, "vkEnumerateInstanceExtensionProperties failed" },
        { nameof(VulkanCall.CreateInstance), 1, "vkCreateInstance failed" },
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Gpu")]
    public void Create_WithAndWithoutValidation(bool validation)
    {
        BindLoader();
        VulkanCallGate gate = new();

        Result<VulkanInstance> result = InstanceFactory.Create(new InstanceFactory.Options { ApplicationName = nameof(InstanceFactoryTests), Validation = validation }, gate);

        Assert.True(result.IsSuccess, result.ToString());
        using VulkanInstance instance = result.Value;
        Assert.False(instance.Handle.IsNull());
        Assert.Equal(validation, instance.Validation);
        Assert.Equal(validation ? [Validation] : [], instance.EnabledLayers.ToArray());
        Assert.Equal(validation, instance.IsExtensionEnabled("VK_EXT_validation_features"));
        Assert.True(!validation || instance.IsExtensionEnabled("VK_EXT_debug_utils"));
        foreach (string extension in instance.EnabledExtensions)
        {
            Assert.True(instance.Brutal.IsExtensionEnabled(extension), extension);
        }

        // A working instance: BRUTAL's constructor enumerated at least one physical device through its table.
        Assert.NotEmpty(instance.Brutal.PhysicalDevices);
        Assert.False(gate.IsLost);
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void Dispose_Twice_NoOp()
    {
        BindLoader();
        VulkanInstance instance = InstanceFactory.Create(new InstanceFactory.Options(), new VulkanCallGate()).Value;

        instance.Dispose();
        instance.Dispose();

        // Without the guard, the second call would destroy the instance and free BRUTAL's function table again.
        Assert.True(instance.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => instance.Handle);
    }

    [Theory]
    [MemberData(nameof(Calls))]
    [Trait("Category", "Gpu")]
    public void InjectedFailure_EachCall_IsResult(string callName, int occurrence, string message)
    {
        VulkanCall call = Enum.Parse<VulkanCall>(callName);
        BindLoader();
        VulkanFaultInjector faults = new();
        faults.FailNth(call, occurrence, VkResult.ErrorOutOfHostMemory);
        VulkanCallGate gate = new() { Faults = faults };

        Result<VulkanInstance> result = InstanceFactory.Create(new InstanceFactory.Options { Validation = true }, gate);

        if (result.TryGetValue(out VulkanInstance? created))
        {
            created.Dispose();
        }

        Assert.Equal(new EngineError(ErrorKind.OutOfMemory, message, (int)VkResult.ErrorOutOfHostMemory), result.Error);

        // The fault fired, so the call was reached in the order the theory data assumes.
        Assert.False(faults.TryTake(call, out _));
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void Create_WrapperThrows_IsResult()
    {
        BindLoader();
        InstanceExtensions.Selection selection = new(["VK_BE_not_an_extension"], [], VkInstanceCreateFlags.None, Validation: false);

        Result<VulkanInstance> result = InstanceFactory.Create(selection, string.Empty, new VulkanCallGate());

        if (result.TryGetValue(out VulkanInstance? created))
        {
            created.Dispose();
        }

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Unsupported, result.Error.Kind);
        Assert.Equal((int)VkResult.ErrorExtensionNotPresent, result.Error.NativeCode);
        Assert.StartsWith("vkCreateInstance failed: ", result.Error.Message, StringComparison.Ordinal);

        // BRUTAL's half-built wrapper is finalized without a native instance; that must not take the process down.
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    [Fact]
    public void Create_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>("options", () => InstanceFactory.Create(null!, new VulkanCallGate()));
        Assert.Throws<ArgumentNullException>("gate", () => InstanceFactory.Create(new InstanceFactory.Options(), null!));
    }

    // The factory's precondition: a backend exists, so BRUTAL's Vulkan class is bound to the system loader.
    private static void BindLoader()
    {
        using Logger logger = new(new LoggerOptions { Sinks = [] });
        Result<VulkanBackend> backend = VulkanBackend.Create(new VulkanBackendOptions { AppDirectory = AppContext.BaseDirectory, Logger = logger });
        Assert.True(backend.IsSuccess, backend.ToString());
    }
}
