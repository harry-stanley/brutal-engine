using System;
using System.Globalization;
using System.Linq;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Graphics.Tests;

/// <summary>The swapchain and frame-status types' defaults, enum values and text forms (docs/design/graphics.md sections 3 and 4).</summary>
public sealed class SwapchainTypesTests
{
    [Fact]
    public void Defaults_MatchDesign()
    {
        var options = new SwapchainOptions();
        SwapchainInfo info = default;
        SwapchainImage image = default;

        Assert.Equal(PresentMode.Fifo, options.PresentMode);
        Assert.Equal(SwapchainFormat.Srgb, options.Format);
        Assert.Null(options.DebugName);

        Assert.Equal(new SwapchainInfo(0, 0, TextureFormat.Undefined, PresentMode.Fifo, 0, SupportsCopyDestination: false), info);
        Assert.True(image.Texture.IsNull);
        Assert.True(image.View.IsNull);
        Assert.Equal((0, 0, TextureFormat.Undefined), (image.Width, image.Height, image.Format));
    }

    public static TheoryData<string> Enums() =>
        [nameof(PresentMode), nameof(SwapchainFormat), nameof(AcquireStatus), nameof(FrameStatus)];

    [Theory]
    [MemberData(nameof(Enums))]
    public void EnumValues_MatchDesign(string type)
    {
        (Type enumType, (string Name, int Value)[] expected) = type switch
        {
            nameof(PresentMode) => (typeof(PresentMode), new[] { ("Fifo", 0), ("FifoRelaxed", 1), ("Mailbox", 2), ("Immediate", 3) }),
            nameof(SwapchainFormat) => (typeof(SwapchainFormat), [("Srgb", 0), ("Unorm", 1)]),
            nameof(AcquireStatus) => (
                typeof(AcquireStatus),
                [("Acquired", 0), ("Recreated", 1), ("Minimised", 2), ("Skipped", 3), ("SurfaceLost", 4), ("DeviceLost", 5)]),
            nameof(FrameStatus) => (typeof(FrameStatus), [("Ready", 0), ("DeviceLost", 1)]),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown enum."),
        };

        Assert.False(enumType.IsDefined(typeof(FlagsAttribute), inherit: false));
        Assert.Equal(typeof(int), Enum.GetUnderlyingType(enumType));
        Assert.Equal(expected, Enum.GetValuesAsUnderlyingType(enumType).Cast<int>().Select(v => (Enum.GetName(enumType, v)!, v)));
    }

    public static TheoryData<string> Records() => [nameof(SwapchainOptions), nameof(SwapchainInfo), nameof(SwapchainImage)];

    [Theory]
    [MemberData(nameof(Records))]
    public void ToString_UnderAnotherCulture_IsInvariant(string type)
    {
        (object value, string expected) = type switch
        {
            nameof(SwapchainOptions) => (
                (object)new SwapchainOptions { PresentMode = PresentMode.Mailbox, Format = SwapchainFormat.Unorm, DebugName = "main" },
                "SwapchainOptions { PresentMode = Mailbox, Format = Unorm, DebugName = main }"),
            nameof(SwapchainInfo) => (
                new SwapchainInfo(-1920, -1080, TextureFormat.Bgra8UnormSrgb, PresentMode.FifoRelaxed, -3, SupportsCopyDestination: true),
                "SwapchainInfo { Width = -1920, Height = -1080, Format = Bgra8UnormSrgb, PresentMode = FifoRelaxed, ImageCount = -3, "
                + "SupportsCopyDestination = True }"),
            nameof(SwapchainImage) => (
                new SwapchainImage(new TextureHandle(new Handle(4, 2)), default, -640, -480, TextureFormat.Rgba8Unorm),
                "SwapchainImage { Texture = Texture(4:2), View = TextureView(null), Width = -640, Height = -480, Format = Rgba8Unorm }"),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown record."),
        };

        CultureInfo original = CultureInfo.CurrentCulture;
        // Swedish writes the minus sign as U+2212, so a culture-sensitive format of the negative values shows.
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
        try
        {
            Assert.NotEqual("-1", (-1).ToString(CultureInfo.CurrentCulture));
            Assert.Equal(expected, value.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void SwapchainOptions_NoDebugName_LeavesItEmpty() =>
        Assert.Equal("SwapchainOptions { PresentMode = Fifo, Format = Srgb, DebugName =  }", new SwapchainOptions().ToString());
}
