using Brutal.VulkanApi;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Swapchains;

// docs/design/graphics-vulkan.md section 12: the gate remembers whether its loss came from a timeout (a present-fence
// wait in a swapchain's Present or Dispose), so later calls report ErrorKind.Timeout; the first loss decides the kind.
public sealed class TimedOutLossTests
{
    private static readonly EngineError _reported = new(ErrorKind.Unavailable, "device is lost", (int)VkResult.ErrorDeviceLost);
    private static readonly EngineError _timedOut = new(ErrorKind.Timeout, "device is lost after a GPU timeout", (int)VkResult.Timeout);

    [Fact]
    public void NewGate_IsNotLostAndReportsTheReportedLossKind()
    {
        VulkanCallGate gate = new();

        Assert.Equal((false, false), (gate.IsLost, gate.LostTimedOut));
        Assert.Equal(_reported, gate.LostError);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MarkLost_NewGate_MarksLostWithItsKind(bool timedOut)
    {
        VulkanCallGate gate = new();

        gate.MarkLost(timedOut);

        Assert.Equal((true, timedOut), (gate.IsLost, gate.LostTimedOut));
        Assert.Equal(timedOut ? _timedOut : _reported, gate.LostError);
    }

    [Fact]
    public void MarkLost_AfterReportedLoss_StaysReported()
    {
        VulkanCallGate gate = new();
        Assert.True(gate.Check(VkResult.ErrorDeviceLost, VulkanCall.QueuePresent).IsFailure);

        gate.MarkLost(timedOut: true);

        Assert.Equal((true, false), (gate.IsLost, gate.LostTimedOut));
        Assert.Equal(_reported, gate.LostError);
    }

    [Fact]
    public void Check_DeviceLostAfterTimeoutLoss_StaysTimedOut()
    {
        VulkanCallGate gate = new();
        gate.MarkLost(timedOut: true);

        Assert.Equal(VkCheck.Error(VkResult.ErrorDeviceLost, VulkanCall.WaitForPresentFence), gate.Check(VkResult.ErrorDeviceLost, VulkanCall.WaitForPresentFence).Error);
        gate.MarkLost(timedOut: false);

        Assert.Equal((true, true), (gate.IsLost, gate.LostTimedOut));
        Assert.Equal(_timedOut, gate.LostError);
    }

    [Fact]
    public void Check_Timeout_DoesNotMarkLost()
    {
        // A timeout result alone is no loss (an acquire's is OutOfDate); only the waits that own the rule mark it.
        VulkanCallGate gate = new();

        Assert.Equal(ErrorKind.Timeout, gate.Check(VkResult.Timeout, VulkanCall.WaitForPresentFence).Error.Kind);

        Assert.Equal((false, false), (gate.IsLost, gate.LostTimedOut));
    }
}
