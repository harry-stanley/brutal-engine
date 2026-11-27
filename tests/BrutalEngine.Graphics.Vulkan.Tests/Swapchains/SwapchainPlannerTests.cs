using System;
using System.Linq;
using Brutal.VulkanApi;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Vulkan.Swapchains;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Swapchains;

// The rules of docs/design/graphics-vulkan.md section 4, "Creation". Every expected value is written out from the note.
public sealed class SwapchainPlannerTests
{
    private const string ZeroExtent = "surface extent is zero";
    private const string NoColorAttachment = "surface doesn't support colour-attachment images";
    private const string NoFormat = "surface offers none of B8G8R8A8 or R8G8B8A8, SRGB or UNORM, with SRGB_NONLINEAR";
    private const string NoCompositeAlpha = "surface reports no composite alpha mode";

    private const VkFormat Bgra8Srgb = VkFormat.B8G8R8A8SRGB;
    private const VkFormat Rgba8Srgb = VkFormat.R8G8B8A8SRGB;
    private const VkFormat Bgra8Unorm = VkFormat.B8G8R8A8UNorm;
    private const VkFormat Rgba8Unorm = VkFormat.R8G8B8A8UNorm;

    private static readonly VkExtent2D _source = new(1000, 500);

    /// <summary>A surface every rule accepts, which each test changes in the one place it is about.</summary>
    private static SurfaceFacts Good => new()
    {
        CurrentExtent = new VkExtent2D(800, 600),
        MinExtent = new VkExtent2D(1, 1),
        MaxExtent = new VkExtent2D(4096, 4096),
        MinImageCount = 2,
        MaxImageCount = 8,
        SupportedCompositeAlpha = VkCompositeAlphaFlagsKHR.OpaqueBitKHR | VkCompositeAlphaFlagsKHR.PreMultipliedBitKHR,
        SupportedUsage = VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferDstBit,
        Formats = [Format(Bgra8Srgb)],
        PresentModes = [VkPresentModeKHR.FifoKHR, VkPresentModeKHR.MailboxKHR],
    };

    private static SwapchainOptions Srgb => new() { Format = SwapchainFormat.Srgb };

    [Fact]
    public void Plan_GoodSurface_FillsEveryField()
    {
        SwapchainPlan plan = PlanOk(Good, _source, new SwapchainOptions { PresentMode = PresentMode.Mailbox });

        SwapchainPlan expected = new(
            800,
            600,
            3,
            VkCompositeAlphaFlagsKHR.OpaqueBitKHR,
            VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferDstBit,
            Bgra8Srgb,
            VkColorSpaceKHR.SRGBNonlinearKHR,
            TextureFormat.Bgra8UnormSrgb,
            PresentMode.Mailbox,
            VkPresentModeKHR.MailboxKHR,
            PresentModeFellBack: false);
        Assert.Equal(expected, plan);
        Assert.True(plan.SupportsCopyDestination);
    }

    // Columns: current extent (-1 = undefined), the source's pixel size, the min and max extent, then the expected
    // extent; an expected 0x0 means the plan fails as Cancelled.
    [Theory]
    [InlineData("defined", 800, 600, 1000, 500, 1, 1, 4096, 4096, 800, 600)]
    [InlineData("defined, source ignored even when zero", 800, 600, 0, 0, 1, 1, 4096, 4096, 800, 600)]
    [InlineData("defined, outside the limits, used as given", 5000, 50, 1000, 500, 64, 64, 4096, 4096, 5000, 50)]
    [InlineData("defined zero", 0, 0, 1000, 500, 1, 1, 4096, 4096, 0, 0)]
    [InlineData("defined zero width", 0, 600, 1000, 500, 1, 1, 4096, 4096, 0, 0)]
    [InlineData("defined zero height", 800, 0, 1000, 500, 1, 1, 4096, 4096, 0, 0)]
    [InlineData("undefined, source within the limits", -1, -1, 1000, 500, 1, 1, 4096, 4096, 1000, 500)]
    [InlineData("undefined, one pixel", -1, -1, 1, 1, 1, 1, 4096, 4096, 1, 1)]
    [InlineData("undefined, one below the minimum", -1, -1, 63, 47, 64, 48, 4096, 2160, 64, 48)]
    [InlineData("undefined, at the minimum", -1, -1, 64, 48, 64, 48, 4096, 2160, 64, 48)]
    [InlineData("undefined, at the maximum", -1, -1, 4096, 2160, 64, 48, 4096, 2160, 4096, 2160)]
    [InlineData("undefined, one past the maximum", -1, -1, 4097, 2161, 64, 48, 4096, 2160, 4096, 2160)]
    [InlineData("undefined, width below and height above", -1, -1, 10, 3000, 64, 48, 4096, 2160, 64, 2160)]
    [InlineData("undefined, width above and height below", -1, -1, 5000, 10, 64, 48, 4096, 2160, 4096, 48)]
    [InlineData("undefined, zero source, checked before clamping", -1, -1, 0, 0, 1, 1, 4096, 4096, 0, 0)]
    [InlineData("undefined, zero source width", -1, -1, 0, 500, 1, 1, 4096, 4096, 0, 0)]
    [InlineData("undefined, zero source height", -1, -1, 1000, 0, 1, 1, 4096, 4096, 0, 0)]
    [InlineData("undefined, negative source", -1, -1, -5, 500, 1, 1, 4096, 4096, 0, 0)]
    [InlineData("undefined, clamped to a zero maximum", -1, -1, 1000, 500, 0, 0, 0, 0, 0, 0)]
    [InlineData("mixed, undefined width only, taken as defined", -1, 600, 1000, 500, 1, 1, 4096, 4096, 0, 0)]
    [InlineData("mixed, undefined height only, taken as defined", 800, -1, 1000, 500, 1, 1, 4096, 4096, 0, 0)]
    public void Extent_Cases(
        string name, int currentWidth, int currentHeight, int sourceWidth, int sourceHeight,
        int minWidth, int minHeight, int maxWidth, int maxHeight, int expectedWidth, int expectedHeight)
    {
        SurfaceFacts facts = Good with
        {
            CurrentExtent = new VkExtent2D(currentWidth, currentHeight),
            MinExtent = new VkExtent2D(minWidth, minHeight),
            MaxExtent = new VkExtent2D(maxWidth, maxHeight),
        };

        Result<SwapchainPlan> result = SwapchainPlanner.Plan(facts, new VkExtent2D(sourceWidth, sourceHeight), Srgb);

        if (expectedWidth == 0)
        {
            AssertFails(result, ErrorKind.Cancelled, ZeroExtent, name);
        }
        else
        {
            Assert.True(result.IsSuccess, $"{name}: {(result.IsFailure ? result.Error.Message : "")}");
            Assert.Equal((expectedWidth, expectedHeight), (result.Value.Width, result.Value.Height));
        }
    }

    [Fact]
    public void Extent_UndefinedSentinel_IsMinusOneInBothFields()
    {
        // 0xFFFFFFFF, which BRUTAL's VkExtent2D holds as int.
        Assert.Equal((-1, -1), (SwapchainPlanner.UndefinedExtent.Width, SwapchainPlanner.UndefinedExtent.Height));
    }

    [Theory]
    [InlineData(2, 0, 3)] // no maximum
    [InlineData(1, 0, 2)]
    [InlineData(2, 4, 3)] // the maximum one past min + 1
    [InlineData(2, 3, 3)] // the maximum at min + 1
    [InlineData(2, 2, 2)] // the maximum caps it
    [InlineData(3, 8, 4)]
    public void ImageCount_Cases(int minImageCount, int maxImageCount, int expected)
    {
        SurfaceFacts facts = Good with { MinImageCount = minImageCount, MaxImageCount = maxImageCount };

        Assert.Equal(expected, PlanOk(facts, _source, Srgb).ImageCount);
    }

    [Theory]
    [InlineData(VkCompositeAlphaFlagsKHR.OpaqueBitKHR, VkCompositeAlphaFlagsKHR.OpaqueBitKHR)]
    [InlineData(
        VkCompositeAlphaFlagsKHR.OpaqueBitKHR | VkCompositeAlphaFlagsKHR.PreMultipliedBitKHR
            | VkCompositeAlphaFlagsKHR.PostMultipliedBitKHR | VkCompositeAlphaFlagsKHR.InheritBitKHR,
        VkCompositeAlphaFlagsKHR.OpaqueBitKHR)]
    [InlineData(VkCompositeAlphaFlagsKHR.OpaqueBitKHR | VkCompositeAlphaFlagsKHR.InheritBitKHR, VkCompositeAlphaFlagsKHR.OpaqueBitKHR)]
    [InlineData(
        VkCompositeAlphaFlagsKHR.PreMultipliedBitKHR | VkCompositeAlphaFlagsKHR.PostMultipliedBitKHR,
        VkCompositeAlphaFlagsKHR.PreMultipliedBitKHR)]
    [InlineData(
        VkCompositeAlphaFlagsKHR.PostMultipliedBitKHR | VkCompositeAlphaFlagsKHR.InheritBitKHR,
        VkCompositeAlphaFlagsKHR.PostMultipliedBitKHR)]
    [InlineData(VkCompositeAlphaFlagsKHR.PostMultipliedBitKHR, VkCompositeAlphaFlagsKHR.PostMultipliedBitKHR)]
    [InlineData(VkCompositeAlphaFlagsKHR.InheritBitKHR, VkCompositeAlphaFlagsKHR.InheritBitKHR)]
    public void CompositeAlpha_Cases(VkCompositeAlphaFlagsKHR supported, VkCompositeAlphaFlagsKHR expected)
    {
        SurfaceFacts facts = Good with { SupportedCompositeAlpha = supported };

        Assert.Equal(expected, PlanOk(facts, _source, Srgb).CompositeAlpha);
    }

    [Fact]
    public void CompositeAlpha_None_Unsupported()
    {
        SurfaceFacts facts = Good with { SupportedCompositeAlpha = VkCompositeAlphaFlagsKHR.None };

        AssertFails(SwapchainPlanner.Plan(facts, _source, Srgb), ErrorKind.Unsupported, NoCompositeAlpha, "no alpha");
    }

    // An expected usage of 0 means the plan fails as Unsupported.
    [Theory]
    [InlineData(VkImageUsageFlags.ColorAttachmentBit, VkImageUsageFlags.ColorAttachmentBit, false)]
    [InlineData(
        VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferDstBit,
        VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferDstBit,
        true)]
    [InlineData(
        VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.TransferSrcBit
            | VkImageUsageFlags.SampledBit | VkImageUsageFlags.StorageBit,
        VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferDstBit,
        true)]
    [InlineData(
        VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferSrcBit | VkImageUsageFlags.SampledBit,
        VkImageUsageFlags.ColorAttachmentBit,
        false)]
    [InlineData(VkImageUsageFlags.TransferDstBit, (VkImageUsageFlags)0, false)]
    [InlineData(
        VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.TransferSrcBit | VkImageUsageFlags.SampledBit
            | VkImageUsageFlags.StorageBit,
        (VkImageUsageFlags)0,
        false)]
    [InlineData((VkImageUsageFlags)0, (VkImageUsageFlags)0, false)]
    public void Usage_Cases(VkImageUsageFlags supported, VkImageUsageFlags expected, bool supportsCopyDestination)
    {
        SurfaceFacts facts = Good with { SupportedUsage = supported };

        Result<SwapchainPlan> result = SwapchainPlanner.Plan(facts, _source, Srgb);

        if (expected == 0)
        {
            AssertFails(result, ErrorKind.Unsupported, NoColorAttachment, supported.ToString());
        }
        else
        {
            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : "");
            Assert.Equal(expected, result.Value.Usage);
            Assert.Equal(supportsCopyDestination, result.Value.SupportsCopyDestination);
        }
    }

    public static TheoryData<VkFormat[], VkFormat, TextureFormat> SrgbOrderCases => new()
    {
        { [Rgba8Unorm, Bgra8Unorm, Rgba8Srgb, Bgra8Srgb], Bgra8Srgb, TextureFormat.Bgra8UnormSrgb },
        { [Rgba8Unorm, Bgra8Unorm, Rgba8Srgb], Rgba8Srgb, TextureFormat.Rgba8UnormSrgb },
        { [Rgba8Unorm, Bgra8Unorm], Bgra8Unorm, TextureFormat.Bgra8Unorm },
        { [Rgba8Unorm], Rgba8Unorm, TextureFormat.Rgba8Unorm },
        { [VkFormat.A2B10G10R10UNormPack32, Rgba8Srgb, VkFormat.R16G16B16A16SFloat], Rgba8Srgb, TextureFormat.Rgba8UnormSrgb },
    };

    public static TheoryData<VkFormat[], VkFormat, TextureFormat> UnormOrderCases => new()
    {
        { [Rgba8Srgb, Bgra8Srgb, Rgba8Unorm, Bgra8Unorm], Bgra8Unorm, TextureFormat.Bgra8Unorm },
        { [Rgba8Srgb, Bgra8Srgb, Rgba8Unorm], Rgba8Unorm, TextureFormat.Rgba8Unorm },
        { [Rgba8Srgb, Bgra8Srgb], Bgra8Srgb, TextureFormat.Bgra8UnormSrgb },
        { [Rgba8Srgb], Rgba8Srgb, TextureFormat.Rgba8UnormSrgb },
        { [VkFormat.A2B10G10R10UNormPack32, Bgra8Srgb, VkFormat.R16G16B16A16SFloat], Bgra8Srgb, TextureFormat.Bgra8UnormSrgb },
    };

    [Theory]
    [MemberData(nameof(SrgbOrderCases))]
    public void Format_SrgbOrder(VkFormat[] offered, VkFormat expected, TextureFormat expectedTexture)
    {
        AssertFormat(SwapchainFormat.Srgb, offered, expected, expectedTexture);
    }

    [Theory]
    [MemberData(nameof(UnormOrderCases))]
    public void Format_UnormOrder(VkFormat[] offered, VkFormat expected, TextureFormat expectedTexture)
    {
        AssertFormat(SwapchainFormat.Unorm, offered, expected, expectedTexture);
    }

    [Theory]
    [InlineData(SwapchainFormat.Srgb)]
    [InlineData(SwapchainFormat.Unorm)]
    public void Format_OtherColorSpace_IsSkipped(SwapchainFormat kind)
    {
        // The first choice of each order is offered only in another colour space, so the next one wins.
        VkFormat first = kind == SwapchainFormat.Srgb ? Bgra8Srgb : Bgra8Unorm;
        VkFormat second = kind == SwapchainFormat.Srgb ? Rgba8Srgb : Rgba8Unorm;
        SurfaceFacts facts = Good with
        {
            Formats = [new VkSurfaceFormatKHR { Format = first, ColorSpace = VkColorSpaceKHR.DisplayP3NonlinearEXT }, Format(second)],
        };

        SwapchainPlan plan = PlanOk(facts, _source, new SwapchainOptions { Format = kind });

        Assert.Equal(second, plan.Format);
        Assert.Equal(VkColorSpaceKHR.SRGBNonlinearKHR, plan.ColorSpace);
    }

    public static TheoryData<string, SwapchainFormat> NoneOfFourCases()
    {
        TheoryData<string, SwapchainFormat> data = [];
        foreach (SwapchainFormat kind in Enum.GetValues<SwapchainFormat>())
        {
            data.Add("null", kind);
            data.Add("empty", kind);
            data.Add("other formats", kind);
            data.Add("the four in another colour space", kind);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(NoneOfFourCases))]
    public void Format_NoneOfFour_Unsupported(string offered, SwapchainFormat kind)
    {
        VkSurfaceFormatKHR[]? formats = offered switch
        {
            "null" => null,
            "empty" => [],
            "other formats" => [Format(VkFormat.A2B10G10R10UNormPack32), Format(VkFormat.R16G16B16A16SFloat)],
            _ =>
            [
                .. new[] { Bgra8Srgb, Rgba8Srgb, Bgra8Unorm, Rgba8Unorm }.Select(
                    f => new VkSurfaceFormatKHR { Format = f, ColorSpace = VkColorSpaceKHR.ExtendedSRGBLinearEXT }),
            ],
        };
        SurfaceFacts facts = Good with { Formats = formats };

        Result<SwapchainPlan> result = SwapchainPlanner.Plan(facts, _source, new SwapchainOptions { Format = kind });

        AssertFails(result, ErrorKind.Unsupported, NoFormat, $"{kind}, {offered}");
    }

    // The literal mapping of the note: each engine mode and its VkPresentModeKHR, used when the surface lists it.
    [Theory]
    [InlineData(PresentMode.Fifo, VkPresentModeKHR.FifoKHR)]
    [InlineData(PresentMode.FifoRelaxed, VkPresentModeKHR.FifoRelaxedKHR)]
    [InlineData(PresentMode.Mailbox, VkPresentModeKHR.MailboxKHR)]
    [InlineData(PresentMode.Immediate, VkPresentModeKHR.ImmediateKHR)]
    public void PresentMode_Listed_IsUsed(PresentMode requested, VkPresentModeKHR expected)
    {
        // Listed last, behind every other mode, so a search that stops early or matches the wrong value fails.
        VkPresentModeKHR[] others = [.. AllVkModes.Where(m => m != expected)];
        SurfaceFacts facts = Good with { PresentModes = [.. others, expected] };

        SwapchainPlan plan = PlanOk(facts, _source, new SwapchainOptions { PresentMode = requested });

        Assert.Equal((requested, expected, false), (plan.PresentMode, plan.VkPresentMode, plan.PresentModeFellBack));
    }

    // The requested mode at index 0 of the surface's list, ahead of every other mode, and as the only mode listed.
    [Theory]
    [InlineData(PresentMode.Fifo, VkPresentModeKHR.FifoKHR, false)]
    [InlineData(PresentMode.Fifo, VkPresentModeKHR.FifoKHR, true)]
    [InlineData(PresentMode.FifoRelaxed, VkPresentModeKHR.FifoRelaxedKHR, false)]
    [InlineData(PresentMode.FifoRelaxed, VkPresentModeKHR.FifoRelaxedKHR, true)]
    [InlineData(PresentMode.Mailbox, VkPresentModeKHR.MailboxKHR, false)]
    [InlineData(PresentMode.Mailbox, VkPresentModeKHR.MailboxKHR, true)]
    [InlineData(PresentMode.Immediate, VkPresentModeKHR.ImmediateKHR, false)]
    [InlineData(PresentMode.Immediate, VkPresentModeKHR.ImmediateKHR, true)]
    public void PresentMode_ListedFirstOrAlone_IsUsed(PresentMode requested, VkPresentModeKHR expected, bool alone)
    {
        VkPresentModeKHR[] modes = alone ? [expected] : [expected, .. AllVkModes.Where(m => m != expected)];
        SurfaceFacts facts = Good with { PresentModes = modes };

        SwapchainPlan plan = PlanOk(facts, _source, new SwapchainOptions { PresentMode = requested });

        Assert.Equal((requested, expected, false), (plan.PresentMode, plan.VkPresentMode, plan.PresentModeFellBack));
    }

    [Fact]
    public void PresentMode_DefaultRequest_FifoListedFirst_IsNoFallback()
    {
        // The default options ask for FIFO, which the good surface lists first, as most drivers do.
        SwapchainPlan plan = PlanOk(Good, _source, new SwapchainOptions());

        Assert.Equal(
            (PresentMode.Fifo, VkPresentModeKHR.FifoKHR, false),
            (plan.PresentMode, plan.VkPresentMode, plan.PresentModeFellBack));
    }

    [Theory]
    [InlineData(PresentMode.Mailbox, "FifoKHR,ImmediateKHR")]
    [InlineData(PresentMode.Immediate, "FifoKHR,MailboxKHR,FifoRelaxedKHR")]
    [InlineData(PresentMode.FifoRelaxed, "FifoKHR")]
    [InlineData(PresentMode.Mailbox, "")]
    [InlineData(PresentMode.Fifo, "MailboxKHR")] // FIFO is required, so a driver that omits it still gets FIFO
    [InlineData(PresentMode.Immediate, null)]
    public void PresentMode_Unlisted_FallsBackToFifo(PresentMode requested, string? offered)
    {
        VkPresentModeKHR[]? modes = offered is null
            ? null
            : [.. offered.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Enum.Parse<VkPresentModeKHR>)];
        SurfaceFacts facts = Good with { PresentModes = modes };

        SwapchainPlan plan = PlanOk(facts, _source, new SwapchainOptions { PresentMode = requested });

        Assert.Equal(
            (PresentMode.Fifo, VkPresentModeKHR.FifoKHR, true),
            (plan.PresentMode, plan.VkPresentMode, plan.PresentModeFellBack));
    }

    [Fact]
    public void Plan_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SwapchainPlanner.Plan(Good, _source, null!));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MinValue)]
    public void Plan_UndefinedPresentMode_Throws(int value)
    {
        SwapchainOptions options = new() { PresentMode = (PresentMode)value };

        Assert.Throws<ArgumentOutOfRangeException>(() => SwapchainPlanner.Plan(Good, _source, options));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MinValue)]
    public void Plan_UndefinedFormatKind_Throws(int value)
    {
        SwapchainOptions options = new() { Format = (SwapchainFormat)value };

        Assert.Throws<ArgumentOutOfRangeException>(() => SwapchainPlanner.Plan(Good, _source, options));
    }

    private static VkPresentModeKHR[] AllVkModes =>
        [VkPresentModeKHR.ImmediateKHR, VkPresentModeKHR.MailboxKHR, VkPresentModeKHR.FifoKHR, VkPresentModeKHR.FifoRelaxedKHR];

    private static VkSurfaceFormatKHR Format(VkFormat format) =>
        new() { Format = format, ColorSpace = VkColorSpaceKHR.SRGBNonlinearKHR };

    private static void AssertFormat(SwapchainFormat kind, VkFormat[] offered, VkFormat expected, TextureFormat expectedTexture)
    {
        SurfaceFacts facts = Good with { Formats = [.. offered.Select(Format)] };

        SwapchainPlan plan = PlanOk(facts, _source, new SwapchainOptions { Format = kind });

        Assert.Equal((expected, VkColorSpaceKHR.SRGBNonlinearKHR, expectedTexture), (plan.Format, plan.ColorSpace, plan.TextureFormat));
    }

    private static SwapchainPlan PlanOk(in SurfaceFacts facts, VkExtent2D source, SwapchainOptions options)
    {
        Result<SwapchainPlan> result = SwapchainPlanner.Plan(facts, source, options);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : "");
        return result.Value;
    }

    private static void AssertFails(Result<SwapchainPlan> result, ErrorKind kind, string message, string row)
    {
        Assert.True(result.IsFailure, $"{row}: expected {kind}, got a plan.");
        Assert.Equal((kind, message, 0), (result.Error.Kind, result.Error.Message, result.Error.NativeCode));
    }
}
