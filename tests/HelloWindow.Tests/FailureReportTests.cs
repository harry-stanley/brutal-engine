using System;
using Brutal.SdlApi;
using Brutal.VulkanApi;
using Xunit;

namespace HelloWindow.Tests;

public sealed class FailureReportTests
{
    [Fact]
    public void NeedsStackTrace_SampleFailure_IsFalse()
    {
        Assert.False(FailureReport.NeedsStackTrace(new SampleFailureException("No suitable device.")));
    }

    [Fact]
    public void NeedsStackTrace_VkResultError_IsFalse()
    {
        Assert.False(FailureReport.NeedsStackTrace(new VkResultError(VkResult.ErrorDeviceLost, "vkQueueSubmit returned ErrorDeviceLost (-4).")));
    }

    [Fact]
    public void NeedsStackTrace_SdlException_IsFalse()
    {
        Assert.False(FailureReport.NeedsStackTrace(new SdlException("SDL_CreateWindow failed: no video device")));
    }

    [Fact]
    public void NeedsStackTrace_SdlCallbackException_IsTrue()
    {
        // It wraps whatever an SDL callback threw, so its own message doesn't say what went wrong.
        var callback = new SdlCallbackException("Exception in SDL event watch", new InvalidOperationException("bug in the watch"));

        Assert.True(FailureReport.NeedsStackTrace(callback));
    }

    [Fact]
    public void NeedsStackTrace_UnexpectedException_IsTrue()
    {
        Assert.True(FailureReport.NeedsStackTrace(new InvalidOperationException("No device yet.")));
    }
}
