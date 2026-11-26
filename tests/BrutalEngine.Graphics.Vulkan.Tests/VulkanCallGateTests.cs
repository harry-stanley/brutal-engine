using System;
using Brutal.VulkanApi;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests;

public sealed class VulkanCallGateTests
{
    private const int MeasuredRounds = 1_000;

    [Fact]
    public void Check_Success_ReturnsSuccessAndKeepsDeviceHealthy()
    {
        VulkanCallGate gate = new();

        Result result = gate.Check(VkResult.Success, VulkanCall.QueueSubmit2);

        Assert.True(result.IsSuccess);
        Assert.False(gate.IsLost);
    }

    [Fact]
    public void DeviceLost_MarksLostAndStays()
    {
        VulkanCallGate gate = new();

        Result lost = gate.Check(VkResult.ErrorDeviceLost, VulkanCall.WaitForFences);

        Assert.True(lost.TryGetError(out EngineError error));
        Assert.Equal(ErrorKind.Unavailable, error.Kind);
        Assert.Equal((int)VkResult.ErrorDeviceLost, error.NativeCode);
        Assert.True(gate.IsLost);

        // Later results, successes included, never clear the flag.
        Assert.True(gate.Check(VkResult.Success, VulkanCall.QueueSubmit2).IsSuccess);
        Assert.True(gate.Check(VkResult.ErrorOutOfHostMemory, VulkanCall.CreateFence).IsFailure);
        Assert.True(gate.IsLost);
    }

    [Theory]
    [MemberData(nameof(VkResultCases.Table), MemberType = typeof(VkResultCases))]
    public void Check_Failure_ReturnsTheCallsErrorAndMarksLossOnlyForDeviceLost(VkResult result, ErrorKind kind)
    {
        VulkanCallGate gate = new();

        Result checkedResult = gate.Check(result, VulkanCall.CreateImageView);

        Assert.True(checkedResult.TryGetError(out EngineError error));
        Assert.Equal(VkCheck.Error(result, VulkanCall.CreateImageView), error);
        Assert.Equal(kind, error.Kind);
        Assert.Equal(result == VkResult.ErrorDeviceLost, gate.IsLost);
    }

    [Fact]
    public void Check_PipelineCall_UsesTheOverride()
    {
        VulkanCallGate gate = new();

        Assert.True(gate.Check(VkResult.ErrorUnknown, VulkanCall.CreateGraphicsPipelines).TryGetError(out EngineError error));

        Assert.Equal(ErrorKind.InvalidData, error.Kind);
    }

    [Fact]
    public void Check_UndefinedCall_Throws()
    {
        VulkanCallGate gate = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => gate.Check(VkResult.ErrorUnknown, (VulkanCall)(-1)));
    }

    [Fact]
    public void Check_DeviceLostWithUndefinedCall_ThrowsWithoutMarkingLost()
    {
        VulkanCallGate gate = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => gate.Check(VkResult.ErrorDeviceLost, (VulkanCall)(-1)));

        // The error is built before the flag is set, so misuse never marks a healthy device lost.
        Assert.False(gate.IsLost);
    }

    [Fact]
    public void Check_DoesNotAllocate()
    {
        VulkanFaultInjector faults = new();
        VulkanCallGate gate = new() { Faults = faults };
        int failures = RunChecks(gate, faults); // warm-up: JIT and first-call costs stay outside the window

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < MeasuredRounds; i++)
        {
            failures += RunChecks(gate, faults);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal((MeasuredRounds + 1) * 4, failures);
        Assert.True(gate.IsLost);
    }

    [Fact]
    public void TryTakeFault_NoInjector_ReturnsFalse()
    {
        VulkanCallGate gate = new();

        Assert.False(gate.TryTakeFault(VulkanCall.QueueSubmit2, out VkResult result));
        Assert.Equal(VkResult.Success, result);
    }

    [Fact]
    public void TryTakeFault_ArmedInjector_ReturnsTheInjectedResult()
    {
        VulkanFaultInjector faults = new();
        faults.FailNth(VulkanCall.QueueSubmit2, 2, VkResult.ErrorDeviceLost);
        VulkanCallGate gate = new() { Faults = faults };

        Assert.False(gate.TryTakeFault(VulkanCall.QueueSubmit2, out _));
        Assert.True(gate.TryTakeFault(VulkanCall.QueueSubmit2, out VkResult injected));

        Assert.Equal(VkResult.ErrorDeviceLost, injected);
        Assert.True(gate.Check(injected, VulkanCall.QueueSubmit2).IsFailure);
        Assert.True(gate.IsLost);
    }

    /// <summary>One round of every outcome the frame path can see; returns the number of failures.</summary>
    private static int RunChecks(VulkanCallGate gate, VulkanFaultInjector faults)
    {
        int failures = 0;
        failures += gate.Check(VkResult.Success, VulkanCall.QueueSubmit2).IsFailure ? 1 : 0;
        failures += gate.Check(VkResult.Timeout, VulkanCall.WaitForFences).IsFailure ? 1 : 0;
        failures += gate.Check(VkResult.ErrorOutOfDeviceMemory, VulkanCall.CreateBuffer).IsFailure ? 1 : 0;
        failures += gate.Check(VkResult.ErrorUnknown, VulkanCall.CreateShaderModule).IsFailure ? 1 : 0;
        failures += gate.Check(VkResult.ErrorDeviceLost, VulkanCall.QueuePresent).IsFailure ? 1 : 0;

        // An armed fault firing, and a counted call that doesn't fire.
        faults.FailNth(VulkanCall.AcquireNextImage, 1, VkResult.ErrorDeviceLost);
        failures += gate.TryTakeFault(VulkanCall.AcquireNextImage, out _) ? 0 : 1;
        failures += gate.TryTakeFault(VulkanCall.AcquireNextImage, out _) ? 1 : 0;
        return failures;
    }
}
