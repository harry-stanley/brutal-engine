using Brutal.VulkanApi;

namespace HelloWindow;

/// <summary>Maps the result of <c>vkAcquireNextImageKHR</c> or <c>vkQueuePresentKHR</c> to what the frame loop does
/// next (docs/03-architecture.md 3.5, "Recoverable GPU events").</summary>
internal static class FrameResultPolicy
{
    /// <summary>Maps a swapchain call's result to an action. Never allocates.</summary>
    /// <param name="result">The result of the acquire or present call (called with an infinite timeout).</param>
    /// <returns>The action and the result it came from.</returns>
    public static FrameDecision Map(VkResult result) => new(
        result switch
        {
            VkResult.Success => FrameAction.Render,
            VkResult.SuboptimalKHR => FrameAction.RenderThenRecreate,
            VkResult.ErrorOutOfDateKHR => FrameAction.Recreate,

            // Includes TIMEOUT and NOT_READY: with an infinite timeout, acquire can't return them.
            _ => FrameAction.Fatal,
        },
        result);
}
