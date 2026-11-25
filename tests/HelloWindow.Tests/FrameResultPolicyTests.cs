using System;
using Brutal.VulkanApi;
using Xunit;

namespace HelloWindow.Tests;

public sealed class FrameResultPolicyTests
{
    [Fact]
    public void Map_Success_Renders()
    {
        Assert.Equal(new FrameDecision(FrameAction.Render, VkResult.Success), FrameResultPolicy.Map(VkResult.Success));
    }

    [Fact]
    public void Map_OutOfDate_Recreates()
    {
        Assert.Equal(FrameAction.Recreate, FrameResultPolicy.Map(VkResult.ErrorOutOfDateKHR).Action);
    }

    [Fact]
    public void Map_Suboptimal_RendersThenRecreates()
    {
        Assert.Equal(FrameAction.RenderThenRecreate, FrameResultPolicy.Map(VkResult.SuboptimalKHR).Action);
    }

    [Theory]
    [InlineData(VkResult.ErrorDeviceLost, "ErrorDeviceLost (-4)")]
    [InlineData(VkResult.ErrorSurfaceLostKHR, "ErrorSurfaceLostKHR")]
    [InlineData(VkResult.ErrorOutOfDeviceMemory, "ErrorOutOfDeviceMemory")]
    [InlineData(VkResult.Timeout, "Timeout (2)")]
    [InlineData(VkResult.NotReady, "NotReady (1)")]
    [InlineData((VkResult)(-123456), "-123456")]
    public void Map_DeviceLostOrUnknown_IsFatalWithName(VkResult result, string expectedName)
    {
        FrameDecision decision = FrameResultPolicy.Map(result);

        Assert.Equal(FrameAction.Fatal, decision.Action);
        Assert.Equal(result, decision.Result);
        Assert.Contains($"vkQueuePresentKHR returned {expectedName}", decision.Describe("vkQueuePresentKHR"), StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_UnnamedResult_PrintsTheNumberOnce()
    {
        FrameDecision decision = FrameResultPolicy.Map((VkResult)(-123456));

        Assert.Equal("vkQueuePresentKHR returned -123456", decision.Describe("vkQueuePresentKHR"));
    }

    [Fact]
    public void Map_RecoverableResults_DoNotAllocate()
    {
        // Runs every frame, so it must not allocate (docs/07-coding-standards.md, hot paths).
        _ = FrameResultPolicy.Map(VkResult.SuboptimalKHR);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            _ = FrameResultPolicy.Map(VkResult.Success);
            _ = FrameResultPolicy.Map(VkResult.SuboptimalKHR);
            _ = FrameResultPolicy.Map(VkResult.ErrorOutOfDateKHR);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
