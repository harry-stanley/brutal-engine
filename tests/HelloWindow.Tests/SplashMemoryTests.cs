using Brutal.VulkanApi;
using HelloWindow.Splash;
using Xunit;

namespace HelloWindow.Tests;

public sealed class SplashMemoryTests
{
    private const VkMemoryPropertyFlags Host = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit;

    private const VkMemoryPropertyFlags Device = VkMemoryPropertyFlags.DeviceLocalBit;

    private static VkPhysicalDeviceMemoryProperties Types(params VkMemoryPropertyFlags[] flags)
    {
        VkPhysicalDeviceMemoryProperties properties = new() { MemoryTypeCount = flags.Length };
        for (int i = 0; i < flags.Length; i++)
        {
            properties.MemoryTypes[i] = new VkMemoryType { PropertyFlags = flags[i] };
        }

        return properties;
    }

    [Fact]
    public void ChooseType_PreferredAvailable_ChoosesIt()
    {
        VkPhysicalDeviceMemoryProperties properties = Types(Host, Device, Device | Host);

        Assert.Equal(1, SplashMemory.ChooseType(properties, 0b111, VkMemoryPropertyFlags.None, Device));
    }

    [Fact]
    public void ChooseType_PreferredMissing_FallsBackToFirstWithRequired()
    {
        VkPhysicalDeviceMemoryProperties properties = Types(VkMemoryPropertyFlags.HostVisibleBit, Host, Host | VkMemoryPropertyFlags.HostCachedBit);

        Assert.Equal(1, SplashMemory.ChooseType(properties, 0b111, Host, Device));
    }

    [Fact]
    public void ChooseType_TypeNotAllowedByResource_IsSkipped()
    {
        VkPhysicalDeviceMemoryProperties properties = Types(Device, Host, Device | Host);

        Assert.Equal(2, SplashMemory.ChooseType(properties, 0b100, VkMemoryPropertyFlags.None, Device));
    }

    [Fact]
    public void ChooseType_NoTypeHasRequired_ReturnsMinusOne()
    {
        VkPhysicalDeviceMemoryProperties properties = Types(Device, Device);

        Assert.Equal(-1, SplashMemory.ChooseType(properties, 0b11, Host, VkMemoryPropertyFlags.None));
    }

    [Fact]
    public void ChooseType_BitsBeyondTypeCount_AreIgnored()
    {
        VkPhysicalDeviceMemoryProperties properties = Types(Device);

        Assert.Equal(-1, SplashMemory.ChooseType(properties, ~0b1, VkMemoryPropertyFlags.None, VkMemoryPropertyFlags.None));
    }
}
