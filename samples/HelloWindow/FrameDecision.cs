using System;
using Brutal.VulkanApi;

namespace HelloWindow;

/// <summary>What to do after a swapchain acquire or present call.</summary>
/// <param name="Action">The action.</param>
/// <param name="Result">The result the action was derived from.</param>
internal readonly record struct FrameDecision(FrameAction Action, VkResult Result)
{
    /// <summary>Describes a fatal result for an error message. Allocates, so call it only when failing.</summary>
    /// <param name="call">The Vulkan function that returned <see cref="Result"/>.</param>
    /// <returns>For example <c>vkQueuePresentKHR returned ErrorDeviceLost (-4)</c>, or just the number for a result
    /// that has no name.</returns>
    public string Describe(string call) =>
        Enum.IsDefined(Result) ? $"{call} returned {Result} ({(int)Result})" : $"{call} returned {(int)Result}";
}
