using System;
using Brutal.VulkanApi;
using Xunit;

namespace HelloWindow.Tests;

public sealed class HelloWindowOptionsTests
{
    [Fact]
    public void TryParse_ValidArguments_ParsesAll()
    {
        string loader = OperatingSystem.IsWindows() ? @"C:\vulkan\vulkan-1.dll" : "/opt/vulkan/libvulkan.so.1";
        string[] args =
        [
            "--headless", "--frames", "120", "--size", "800x600", "--validation", "--exercise-resize",
            "--require-cpu", "--require-driver", "mesallvmpipe", "--vulkan-loader", loader,
        ];

        Assert.True(HelloWindowOptions.TryParse(args, out HelloWindowOptions? options, out string error));

        Assert.Equal(string.Empty, error);
        Assert.Equal(
            new HelloWindowOptions
            {
                Headless = true,
                Frames = 120,
                Width = 800,
                Height = 600,
                Validation = true,
                ExerciseResize = true,
                RequireCpu = true,
                RequireDriver = VkDriverId.MESALlvmpipe,
                VulkanLoader = loader,
            },
            options);
    }

    [Fact]
    public void TryParse_NoArguments_UsesDefaults()
    {
        Assert.True(HelloWindowOptions.TryParse([], out HelloWindowOptions? options, out _));

        Assert.Equal(new HelloWindowOptions(), options);
        Assert.Equal((1280, 720), (options.Width, options.Height));
        Assert.Null(options.Frames);
        Assert.False(options.Validation);
    }

    [Fact]
    public void TryParse_InjectValidationError_ImpliesValidation()
    {
        Assert.True(HelloWindowOptions.TryParse(["--inject-validation-error"], out HelloWindowOptions? options, out _));

        Assert.True(options.InjectValidationError);
        Assert.True(options.Validation);
    }

    [Fact]
    public void TryParse_HeadlessWithoutFrames_Fails()
    {
        Assert.False(HelloWindowOptions.TryParse(["--headless"], out HelloWindowOptions? options, out string error));

        Assert.Null(options);
        Assert.Contains("--headless", error, StringComparison.Ordinal);
        Assert.Contains("--frames", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_ExerciseResizeWithoutFrames_Fails()
    {
        Assert.False(HelloWindowOptions.TryParse(["--exercise-resize"], out _, out string error));

        Assert.Contains("--exercise-resize", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("NotADriver")]
    [InlineData("1")]
    [InlineData("-1")]
    [InlineData("+3")]
    [InlineData("AmdProprietary,AmdOpenSource")]
    [InlineData("Moltenvk, Moltenvk")]
    [InlineData("Moltenvk ")]
    [InlineData("")]
    [InlineData("MaxEnum")]
    [InlineData("maxenum")]
    public void TryParse_UnknownOrNumericDriverName_Fails(string name)
    {
        Assert.False(HelloWindowOptions.TryParse(["--require-driver", name], out HelloWindowOptions? options, out string error));

        Assert.Null(options);
        Assert.Contains($"'{name}' is not a VkDriverId name", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("12abc")]
    [InlineData(" 7")]
    [InlineData("99999999999")]
    public void TryParse_InvalidFrameCount_Fails(string frames)
    {
        Assert.False(HelloWindowOptions.TryParse(["--frames", frames], out _, out string error));

        Assert.Contains("--frames", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1280")]
    [InlineData("0x720")]
    [InlineData("1280x0")]
    [InlineData("x720")]
    [InlineData("1280x-720")]
    [InlineData("20000x720")]
    [InlineData("1280x720x1")]
    public void TryParse_InvalidSize_Fails(string size)
    {
        Assert.False(HelloWindowOptions.TryParse(["--size", size], out _, out string error));

        Assert.Contains("--size", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--frames")]
    [InlineData("--size")]
    [InlineData("--require-driver")]
    [InlineData("--vulkan-loader")]
    public void TryParse_MissingValue_Fails(string option)
    {
        Assert.False(HelloWindowOptions.TryParse([option], out _, out string error));

        Assert.Contains(option, error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_RelativeLoaderPath_Fails()
    {
        Assert.False(HelloWindowOptions.TryParse(["--vulkan-loader", "vulkan-1.dll"], out _, out string error));

        Assert.Contains("absolute path", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--fullscreen")]
    [InlineData("frames")]
    [InlineData("-headless")]
    public void TryParse_UnknownOption_Fails(string option)
    {
        Assert.False(HelloWindowOptions.TryParse([option], out _, out string error));

        Assert.Contains($"'{option}'", error, StringComparison.Ordinal);
    }
}
