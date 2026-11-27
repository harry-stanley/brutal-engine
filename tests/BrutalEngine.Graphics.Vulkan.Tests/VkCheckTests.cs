using System;
using System.Collections.Generic;
using System.Linq;
using Brutal.VulkanApi;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests;

public sealed class VkCheckTests
{
    private static readonly VulkanCall[] _pipelineCalls =
        [VulkanCall.CreateShaderModule, VulkanCall.CreateGraphicsPipelines, VulkanCall.CreateComputePipelines];

    [Theory]
    [MemberData(nameof(VkResultCases.Table), MemberType = typeof(VkResultCases))]
    public void KindOf_Table(VkResult result, ErrorKind expected)
    {
        Assert.Equal(expected, VkCheck.KindOf(result));
    }

    [Fact]
    public void KindOf_Table_CoversEveryNegativeValueAndTimeout()
    {
        // Guards the theory's data. VkResultCases.Failures is itself built from Enum.GetValues<VkResult>(), so a new
        // negative value in BRUTAL's enum is tested as soon as the pin moves; the first assertion only catches a
        // hand edit to Failures that drops a negative value or TIMEOUT.
        VkResult[] required = [.. Enum.GetValues<VkResult>().Where(r => (int)r < 0).Append(VkResult.Timeout).Distinct()];

        Assert.All(required, r => Assert.Contains(r, VkResultCases.Failures));
        Assert.All(VkResultCases.Specified.Keys, r => Assert.Contains(r, VkResultCases.Failures));
    }

    [Fact]
    public void PipelineCalls_NativeBecomesInvalidData()
    {
        foreach (VulkanCall call in Enum.GetValues<VulkanCall>())
        {
            bool isPipelineCall = _pipelineCalls.Contains(call);
            foreach (VkResult result in VkResultCases.Failures)
            {
                ErrorKind kind = VkResultCases.ExpectedKind(result);
                ErrorKind expected = isPipelineCall && kind == ErrorKind.Native ? ErrorKind.InvalidData : kind;

                Assert.True(expected == VkCheck.Error(result, call).Kind, $"{call}, {result}: expected {expected}.");
            }
        }
    }

    // SwapchainStatusOf: section 4's table, written out from the note, for both an acquire and a present.
    [Theory]
    [InlineData(VkResult.Success, true, SwapchainStatus.Success)]
    [InlineData(VkResult.Success, false, SwapchainStatus.Success)]
    [InlineData(VkResult.SuboptimalKHR, true, SwapchainStatus.Suboptimal)]
    [InlineData(VkResult.SuboptimalKHR, false, SwapchainStatus.Suboptimal)]
    [InlineData(VkResult.ErrorOutOfDateKHR, true, SwapchainStatus.OutOfDate)]
    [InlineData(VkResult.ErrorOutOfDateKHR, false, SwapchainStatus.OutOfDate)]
    [InlineData(VkResult.ErrorSurfaceLostKHR, true, SwapchainStatus.SurfaceLost)]
    [InlineData(VkResult.ErrorSurfaceLostKHR, false, SwapchainStatus.SurfaceLost)]
    [InlineData(VkResult.Timeout, true, SwapchainStatus.OutOfDate)]
    [InlineData(VkResult.Timeout, false, SwapchainStatus.DeviceLost)]
    [InlineData(VkResult.NotReady, true, SwapchainStatus.OutOfDate)]
    [InlineData(VkResult.NotReady, false, SwapchainStatus.DeviceLost)]
    [InlineData(VkResult.ErrorDeviceLost, true, SwapchainStatus.DeviceLost)]
    [InlineData(VkResult.ErrorDeviceLost, false, SwapchainStatus.DeviceLost)]
    [InlineData(VkResult.ErrorOutOfHostMemory, true, SwapchainStatus.DeviceLost)]
    [InlineData(VkResult.ErrorOutOfDeviceMemory, false, SwapchainStatus.DeviceLost)]
    [InlineData(VkResult.ErrorUnknown, true, SwapchainStatus.DeviceLost)]
    [InlineData(VkResult.ErrorUnknown, false, SwapchainStatus.DeviceLost)]
    [InlineData(VkResult.Incomplete, true, SwapchainStatus.DeviceLost)]
    [InlineData(VkResult.Incomplete, false, SwapchainStatus.DeviceLost)]
    [InlineData((VkResult)(-999_999), true, SwapchainStatus.DeviceLost)]
    [InlineData((VkResult)999_999, false, SwapchainStatus.DeviceLost)]
    public void SwapchainStatusOf_Table(VkResult result, bool acquire, SwapchainStatus expected)
    {
        Assert.Equal(expected, VkCheck.SwapchainStatusOf(result, acquire));
    }

    [Fact]
    public void SwapchainStatusOf_EveryUnnamedValue_IsDeviceLost()
    {
        // Every value of the pinned enum, plus unnamed ones, that the table's named rows don't cover.
        VkResult[] named =
        [
            VkResult.SuboptimalKHR, VkResult.ErrorOutOfDateKHR, VkResult.ErrorSurfaceLostKHR, VkResult.Timeout,
            VkResult.NotReady,
        ];

        foreach (VkResult result in VkResultCases.Failures.Where(r => !named.Contains(r)))
        {
            Assert.True(SwapchainStatus.DeviceLost == VkCheck.SwapchainStatusOf(result, acquire: true), $"{result}, acquire");
            Assert.True(SwapchainStatus.DeviceLost == VkCheck.SwapchainStatusOf(result, acquire: false), $"{result}, present");
        }
    }

    [Fact]
    public void SwapchainStatusOf_DoesNotAllocate()
    {
        // Every value of the table and of the pinned enum, for both an acquire and a present.
        VkResult[] values = [VkResult.Success, .. VkResultCases.Failures];
        int sum = SumStatuses(values); // warm-up: JIT and first-call costs stay outside the window

        long before = GC.GetAllocatedBytesForCurrentThread();
        sum += SumStatuses(values);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(sum > 0);
    }

    [Fact]
    public void Error_EveryCall_HasItsOwnConstantMessageAndTheCode()
    {
        HashSet<string> messages = new(StringComparer.Ordinal);
        foreach (VulkanCall call in Enum.GetValues<VulkanCall>())
        {
            EngineError first = VkCheck.Error(VkResult.ErrorOutOfDeviceMemory, call);
            EngineError second = VkCheck.Error(VkResult.ErrorUnknown, call);

            Assert.Equal((int)VkResult.ErrorOutOfDeviceMemory, first.NativeCode);
            Assert.Equal((int)VkResult.ErrorUnknown, second.NativeCode);
            Assert.False(string.IsNullOrWhiteSpace(first.Message));
            Assert.Same(first.Message, second.Message);
            Assert.True(messages.Add(first.Message), $"{call} shares its message with another call: {first.Message}");
        }
    }

    [Fact]
    public void Error_Success_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VkCheck.Error(VkResult.Success, VulkanCall.CreateBuffer));
    }

    [Fact]
    public void Error_UndefinedCall_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VkCheck.Error(VkResult.ErrorUnknown, (VulkanCall)(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VkCheck.Error(VkResult.ErrorUnknown, (VulkanCall)Enum.GetValues<VulkanCall>().Length));
    }

    [Fact]
    public void FromException_MapsTheResultAndNamesTheCall()
    {
        VkResultError exception = new(VkResult.ErrorIncompatibleDriver, "no driver");

        EngineError error = VkCheck.FromException(exception, "vkCreateInstance");

        Assert.Equal(ErrorKind.Unsupported, error.Kind);
        Assert.Equal((int)VkResult.ErrorIncompatibleDriver, error.NativeCode);
        Assert.Equal("vkCreateInstance failed: no driver", error.Message);
    }

    [Fact]
    public void FromException_DeviceLost_IsUnavailable()
    {
        EngineError error = VkCheck.FromException(new VkResultError(VkResult.ErrorDeviceLost, "lost"), "vkCreateDevice");

        Assert.Equal(ErrorKind.Unavailable, error.Kind);
        Assert.Equal((int)VkResult.ErrorDeviceLost, error.NativeCode);
    }

    [Fact]
    public void FromException_InvalidArguments_Throw()
    {
        VkResultError exception = new(VkResult.ErrorUnknown, "x");

        Assert.Throws<ArgumentNullException>(() => VkCheck.FromException(null!, "vkCreateInstance"));
        Assert.Throws<ArgumentNullException>(() => VkCheck.FromException(exception, null!));
        Assert.Throws<ArgumentException>(() => VkCheck.FromException(exception, ""));
    }

    private static int SumStatuses(VkResult[] values)
    {
        int sum = 0;
        foreach (VkResult result in values)
        {
            sum += (int)VkCheck.SwapchainStatusOf(result, acquire: true);
            sum += (int)VkCheck.SwapchainStatusOf(result, acquire: false);
        }

        return sum;
    }
}
