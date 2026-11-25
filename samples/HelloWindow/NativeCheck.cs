using System;
using Brutal.SdlApi;
using Brutal.VulkanApi;

namespace HelloWindow;

/// <summary>Status checks for native calls whose BRUTAL binding returns a status instead of throwing
/// (docs/03-architecture.md 3.5: one helper per library).</summary>
internal static class NativeCheck
{
    /// <summary>Throws unless a Vulkan call succeeded.</summary>
    /// <param name="result">The call's result.</param>
    /// <param name="call">The Vulkan function, for the message.</param>
    /// <exception cref="VkResultError"><paramref name="result"/> is not <see cref="VkResult.Success"/>.</exception>
    public static void Vk(VkResult result, string call)
    {
        if (result != VkResult.Success)
        {
            // An unnamed value formats as its number, so print the number only once.
            throw new VkResultError(result, Enum.IsDefined(result) ? $"{call} returned {result} ({(int)result})." : $"{call} returned {(int)result}.");
        }
    }

    /// <summary>Throws with SDL's error text unless an SDL call succeeded.</summary>
    /// <param name="succeeded">The call's result (SDL functions return false on failure).</param>
    /// <param name="call">The SDL function, for the message.</param>
    /// <exception cref="SdlException"><paramref name="succeeded"/> is false.</exception>
    public static void Sdl(bool succeeded, string call)
    {
        if (!succeeded)
        {
            throw SdlException.FromError($"{call} failed");
        }
    }
}
