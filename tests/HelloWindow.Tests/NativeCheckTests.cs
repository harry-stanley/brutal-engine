using Brutal.VulkanApi;
using Xunit;

namespace HelloWindow.Tests;

public sealed class NativeCheckTests
{
    [Fact]
    public void Vk_Success_DoesNotThrow()
    {
        NativeCheck.Vk(VkResult.Success, "vkQueueSubmit");
    }

    [Fact]
    public void Vk_NamedResult_PrintsNameAndNumber()
    {
        VkResultError error = Assert.Throws<VkResultError>(() => NativeCheck.Vk(VkResult.ErrorDeviceLost, "vkQueueSubmit"));

        Assert.Equal(VkResult.ErrorDeviceLost, error.Result);
        Assert.Equal("vkQueueSubmit returned ErrorDeviceLost (-4).", error.Message);
    }

    [Theory]
    [InlineData(-12345)]
    [InlineData(1000999)]
    public void Vk_UnnamedResult_PrintsNumberOnce(int value)
    {
        VkResultError error = Assert.Throws<VkResultError>(() => NativeCheck.Vk((VkResult)value, "vkQueueSubmit"));

        Assert.Equal((VkResult)value, error.Result);
        Assert.Equal($"vkQueueSubmit returned {value}.", error.Message);
    }
}
