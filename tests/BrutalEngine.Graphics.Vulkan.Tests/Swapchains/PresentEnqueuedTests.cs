using Brutal.VulkanApi;
using BrutalEngine.Graphics.Vulkan.Swapchains;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Swapchains;

// docs/design/graphics-vulkan.md section 4, "Present fences": a present fence is armed again only when its present was
// enqueued, which the specification says of SUCCESS, SUBOPTIMAL_KHR, ERROR_OUT_OF_DATE_KHR, ERROR_SURFACE_LOST_KHR and
// ERROR_FULL_SCREEN_EXCLUSIVE_MODE_LOST_EXT. No driver can be made to return most of these, so the table is pinned here.
// Values are literals from BRUTAL's binding, Brutal.Vulkan/Generated/Enums/VkResult.gen.cs.
public sealed class PresentEnqueuedTests
{
    [Theory]
    [InlineData(0, true)] // Success, :140
    [InlineData(1000001003, true)] // SuboptimalKHR, :280
    [InlineData(-1000001004, true)] // ErrorOutOfDateKHR, :285
    [InlineData(-1000000000, true)] // ErrorSurfaceLostKHR, :270
    [InlineData(-1000255000, true)] // ErrorFullScreenExclusiveModeLostEXT, :340
    [InlineData(-1, false)] // ErrorOutOfHostMemory, :170
    [InlineData(-2, false)] // ErrorOutOfDeviceMemory, :175
    [InlineData(-4, false)] // ErrorDeviceLost, :185
    [InlineData(-13, false)] // ErrorUnknown, :230
    [InlineData(-123456, false)] // no VkResult member
    public void WasEnqueued_RealResult_MatchesTheSpecification(int result, bool enqueued) =>
        Assert.Equal(enqueued, PresentFences.WasEnqueued((VkResult)result, injected: false));

    [Theory]
    [InlineData(0)] // Success
    [InlineData(-1000001004)] // ErrorOutOfDateKHR
    public void WasEnqueued_InjectedResult_IsNeverEnqueued(int result) =>
        Assert.False(PresentFences.WasEnqueued((VkResult)result, injected: true));
}
