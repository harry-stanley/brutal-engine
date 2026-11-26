using System;
using System.Collections.Generic;
using System.Linq;
using Brutal.VulkanApi;
using BrutalEngine.Core;
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
}
