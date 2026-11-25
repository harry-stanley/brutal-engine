using Brutal.VulkanApi;

namespace HelloWindow.Splash;

/// <summary>Chooses Vulkan memory types for the splash's staging buffer and logo image.</summary>
internal static class SplashMemory
{
    /// <summary>Chooses the first memory type that the resource allows and that has every required property,
    /// preferring one that also has every preferred property.</summary>
    /// <param name="properties">The physical device's memory types (<c>vkGetPhysicalDeviceMemoryProperties</c>).</param>
    /// <param name="typeBits">The resource's allowed types (<c>VkMemoryRequirements.memoryTypeBits</c>), one bit per
    /// type index.</param>
    /// <param name="required">Properties the type must have.</param>
    /// <param name="preferred">Properties the type should also have, if any allowed type does.</param>
    /// <returns>The memory type index, or -1 if no allowed type has the required properties.</returns>
    public static int ChooseType(in VkPhysicalDeviceMemoryProperties properties, int typeBits, VkMemoryPropertyFlags required, VkMemoryPropertyFlags preferred)
    {
        int fallback = -1;
        int count = System.Math.Min(properties.MemoryTypeCount, 32);
        for (int i = 0; i < count; i++)
        {
            VkMemoryPropertyFlags flags = properties.MemoryTypes[i].PropertyFlags;
            if ((typeBits & (1 << i)) == 0 || (flags & required) != required)
            {
                continue;
            }

            if ((flags & preferred) == preferred)
            {
                return i;
            }

            if (fallback < 0)
            {
                fallback = i;
            }
        }

        return fallback;
    }
}
