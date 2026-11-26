using System;
using System.Collections.Generic;
using System.Linq;
using Brutal.VulkanApi;
using CsCheck;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests;

public sealed class VulkanFaultInjectorTests
{
    private static readonly VulkanCall[] _calls = Enum.GetValues<VulkanCall>();

    [Fact]
    public void FailNth_FiresOnceAtN()
    {
        // A target call, an occurrence, an injected result, and a run of calls where the target is common, so the
        // run often reaches the occurrence and often passes it.
        Gen<(VulkanCall Target, int Occurrence, VkResult Result, VulkanCall[] Run)> cases =
            from target in Gen.Int[0, _calls.Length - 1]
            from occurrence in Gen.Int[1, 12]
            from result in Gen.Int[0, VkResultCases.Failures.Count - 1]
            from run in Gen.Frequency((3, Gen.Const(target)), (2, Gen.Int[0, _calls.Length - 1])).Array[0, 60]
            select ((VulkanCall)target, occurrence, VkResultCases.Failures[result], run.Select(c => (VulkanCall)c).ToArray());

        cases.Sample(c =>
        {
            VulkanFaultInjector faults = new();
            faults.FailNth(c.Target, c.Occurrence, c.Result);

            List<int> fired = [];
            int targetCalls = 0;
            int expectedIndex = -1;
            for (int i = 0; i < c.Run.Length; i++)
            {
                if (c.Run[i] == c.Target && ++targetCalls == c.Occurrence)
                {
                    expectedIndex = i;
                }

                if (faults.TryTake(c.Run[i], out VkResult injected))
                {
                    fired.Add(i);
                    Assert.Equal(c.Result, injected);
                }
                else
                {
                    Assert.Equal(VkResult.Success, injected);
                }
            }

            Assert.Equal(expectedIndex < 0 ? [] : [expectedIndex], fired);
        });
    }

    [Fact]
    public void TryTake_NothingArmed_ReturnsFalseForEveryCall()
    {
        VulkanFaultInjector faults = new();

        Assert.All(_calls, call => Assert.False(faults.TryTake(call, out _)));
    }

    [Fact]
    public void FailNth_AfterTheFaultFired_CanArmAgain()
    {
        VulkanFaultInjector faults = new();
        faults.FailNth(VulkanCall.WaitForFences, 1, VkResult.Timeout);
        Assert.True(faults.TryTake(VulkanCall.WaitForFences, out _));

        faults.FailNth(VulkanCall.WaitForFences, 1, VkResult.ErrorDeviceLost);

        Assert.True(faults.TryTake(VulkanCall.WaitForFences, out VkResult result));
        Assert.Equal(VkResult.ErrorDeviceLost, result);
    }

    [Fact]
    public void FailNth_AlreadyArmed_Throws()
    {
        VulkanFaultInjector faults = new();
        faults.FailNth(VulkanCall.CreateBuffer, 3, VkResult.ErrorOutOfDeviceMemory);

        Assert.Throws<InvalidOperationException>(() => faults.FailNth(VulkanCall.CreateBuffer, 1, VkResult.ErrorDeviceLost));
    }

    [Fact]
    public void FailNth_InvalidArguments_Throw()
    {
        VulkanFaultInjector faults = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => faults.FailNth(VulkanCall.CreateBuffer, 0, VkResult.ErrorDeviceLost));
        Assert.Throws<ArgumentOutOfRangeException>(() => faults.FailNth(VulkanCall.CreateBuffer, -1, VkResult.ErrorDeviceLost));
        Assert.Throws<ArgumentOutOfRangeException>(() => faults.FailNth((VulkanCall)_calls.Length, 1, VkResult.ErrorDeviceLost));
        Assert.Throws<ArgumentException>(() => faults.FailNth(VulkanCall.CreateBuffer, 1, VkResult.Success));
    }

    [Fact]
    public void TryTake_UndefinedCall_Throws()
    {
        VulkanFaultInjector faults = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => faults.TryTake((VulkanCall)(-1), out _));
    }
}
