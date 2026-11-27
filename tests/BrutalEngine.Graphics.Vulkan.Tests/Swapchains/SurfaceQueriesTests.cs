using System;
using System.Collections.Generic;
using Brutal.VulkanApi;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Vulkan.Devices;
using BrutalEngine.Graphics.Vulkan.Swapchains;
using BrutalEngine.Graphics.Vulkan.Tests.Devices;
using BrutalEngine.Graphics.Vulkan.Tests.Validation;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Swapchains;

// docs/design/graphics-vulkan.md section 4, "Surfaces" and "Creation": the surface facts the planner reads.
public sealed unsafe class SurfaceQueriesTests
{
    // InstanceExtensions.MaxEnumerationAttempts, as the note states it (section 1).
    private const int Attempts = 8;

    private const string CapabilitiesFailed = "vkGetPhysicalDeviceSurfaceCapabilitiesKHR failed";
    private const string FormatsFailed = "vkGetPhysicalDeviceSurfaceFormatsKHR failed";
    private const string ModesFailed = "vkGetPhysicalDeviceSurfacePresentModesKHR failed";

    private static readonly VkSurfaceFormatKHR[] _formats =
    [
        new() { Format = VkFormat.B8G8R8A8SRGB, ColorSpace = VkColorSpaceKHR.SRGBNonlinearKHR },
        new() { Format = VkFormat.R8G8B8A8UNorm, ColorSpace = VkColorSpaceKHR.SRGBNonlinearKHR },
        new() { Format = VkFormat.A2B10G10R10UNormPack32, ColorSpace = VkColorSpaceKHR.Hdr10St2084EXT },
    ];

    private static readonly VkPresentModeKHR[] _modes = [VkPresentModeKHR.MailboxKHR, VkPresentModeKHR.FifoKHR, VkPresentModeKHR.ImmediateKHR];

    // Every field distinct, so a copy from the wrong field fails.
    private static VkSurfaceCapabilitiesKHR Capabilities => new()
    {
        MinImageCount = 2,
        MaxImageCount = 7,
        CurrentExtent = new VkExtent2D(640, 480),
        MinImageExtent = new VkExtent2D(3, 5),
        MaxImageExtent = new VkExtent2D(4093, 4091),
        MaxImageArrayLayers = 1,
        SupportedTransforms = VkSurfaceTransformFlagsKHR.IdentityBitKHR | VkSurfaceTransformFlagsKHR.Rotate90BitKHR,
        CurrentTransform = VkSurfaceTransformFlagsKHR.Rotate90BitKHR,
        SupportedCompositeAlpha = VkCompositeAlphaFlagsKHR.OpaqueBitKHR | VkCompositeAlphaFlagsKHR.InheritBitKHR,
        SupportedUsageFlags = VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferDstBit,
    };

    // The call, which of its native calls fails (1 the count, 2 the fill), the injected result, the kind and message
    // section 13 gives it, and how many native calls ran before it: capabilities, format count and fill, mode count and
    // fill. A scripted single surface, so the occurrence is exactly the call named. The call is passed as its number,
    // because VulkanCall is internal.
    public static TheoryData<int, int, VkResult, ErrorKind, string, int[]> Injected => new()
    {
        { (int)VulkanCall.GetSurfaceCapabilities, 1, VkResult.ErrorSurfaceLostKHR, ErrorKind.Native, CapabilitiesFailed, [0, 0, 0, 0, 0] },
        { (int)VulkanCall.GetSurfaceFormats, 1, VkResult.ErrorOutOfHostMemory, ErrorKind.OutOfMemory, FormatsFailed, [1, 0, 0, 0, 0] },
        { (int)VulkanCall.GetSurfaceFormats, 2, VkResult.ErrorDeviceLost, ErrorKind.Unavailable, FormatsFailed, [1, 1, 0, 0, 0] },
        { (int)VulkanCall.GetSurfacePresentModes, 1, VkResult.ErrorOutOfDeviceMemory, ErrorKind.OutOfMemory, ModesFailed, [1, 1, 1, 0, 0] },
        { (int)VulkanCall.GetSurfacePresentModes, 2, VkResult.ErrorSurfaceLostKHR, ErrorKind.Native, ModesFailed, [1, 1, 1, 1, 0] },
    };

    [Fact]
    public void Read_FakeSurface_CopiesEveryField()
    {
        FakeSurface surface = new() { Capabilities = Capabilities, Formats = _formats, Modes = _modes };

        SurfaceFacts facts = ReadOk(surface);

        Assert.Equal((640, 480), (facts.CurrentExtent.Width, facts.CurrentExtent.Height));
        Assert.Equal((3, 5), (facts.MinExtent.Width, facts.MinExtent.Height));
        Assert.Equal((4093, 4091), (facts.MaxExtent.Width, facts.MaxExtent.Height));
        Assert.Equal((2, 7), (facts.MinImageCount, facts.MaxImageCount));
        Assert.Equal(VkSurfaceTransformFlagsKHR.Rotate90BitKHR, facts.CurrentTransform);
        Assert.Equal(VkCompositeAlphaFlagsKHR.OpaqueBitKHR | VkCompositeAlphaFlagsKHR.InheritBitKHR, facts.SupportedCompositeAlpha);
        Assert.Equal(VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferDstBit, facts.SupportedUsage);
        Assert.Equal(_formats, facts.Formats);
        Assert.Equal(_modes, facts.PresentModes);
        Assert.Equal((1, 1, 1, 1, 1), surface.CallCounts);
    }

    [Fact]
    public void Formats_PersistentIncomplete_FailsAfterLimit()
    {
        FakeSurface surface = new() { Capabilities = Capabilities, Formats = _formats, Modes = _modes, FormatGrowths = int.MaxValue };

        Result<SurfaceFacts> result = SurfaceQueries.Read(surface, new VulkanCallGate());

        Assert.Equal(new EngineError(ErrorKind.Native, FormatsFailed, (int)VkResult.Incomplete), result.Error);
        Assert.Equal((1, Attempts, Attempts, 0, 0), surface.CallCounts);
    }

    [Fact]
    public void PresentModes_PersistentIncomplete_FailsAfterLimit()
    {
        FakeSurface surface = new() { Capabilities = Capabilities, Formats = _formats, Modes = _modes, ModeGrowths = int.MaxValue };

        Result<SurfaceFacts> result = SurfaceQueries.Read(surface, new VulkanCallGate());

        Assert.Equal(new EngineError(ErrorKind.Native, ModesFailed, (int)VkResult.Incomplete), result.Error);
        Assert.Equal((1, 1, 1, Attempts, Attempts), surface.CallCounts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Incomplete_UntilTheLastAttempt_Succeeds(bool formats)
    {
        // The list grows by one entry between the count and the fill on every attempt but the last, so each fill
        // reports VK_INCOMPLETE until the eighth: the bound is inclusive, and each retry must use the count it queried
        // again, ending with the whole, larger list (3 + 7 entries).
        FakeSurface surface = new()
        {
            Capabilities = Capabilities,
            Formats = _formats,
            Modes = _modes,
            FormatGrowths = formats ? Attempts - 1 : 0,
            ModeGrowths = formats ? 0 : Attempts - 1,
        };

        SurfaceFacts facts = ReadOk(surface);

        Assert.Equal(formats ? _formats.Length + Attempts - 1 : _formats.Length, facts.Formats!.Length);
        Assert.Equal(formats ? _modes.Length : _modes.Length + Attempts - 1, facts.PresentModes!.Length);
        Assert.Equal(surface.CurrentFormats, facts.Formats);
        Assert.Equal(surface.CurrentModes, facts.PresentModes);
        Assert.Equal(_formats, facts.Formats[.._formats.Length]);
        Assert.Equal(_modes, facts.PresentModes[.._modes.Length]);
        Assert.Equal(formats ? (1, Attempts, Attempts, 1, 1) : (1, 1, 1, Attempts, Attempts), surface.CallCounts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Fill_WritesFewer_ReadsOnlyThose(bool formats)
    {
        // Three counted, two written (the third entry of the array stays zero, a real-looking UNDEFINED or IMMEDIATE).
        FakeSurface surface = new()
        {
            Capabilities = Capabilities,
            Formats = _formats,
            Modes = _modes,
            FormatsWritten = formats ? 2 : null,
            ModesWritten = formats ? null : 2,
        };

        SurfaceFacts facts = ReadOk(surface);

        Assert.Equal(formats ? _formats[..2] : _formats, facts.Formats);
        Assert.Equal(formats ? _modes : _modes[..2], facts.PresentModes);
    }

    [Fact]
    public void Read_NoFormatsOrModes_ReturnsEmptyWithoutFilling()
    {
        FakeSurface surface = new() { Capabilities = Capabilities };

        SurfaceFacts facts = ReadOk(surface);

        Assert.Empty(facts.Formats!);
        Assert.Empty(facts.PresentModes!);
        Assert.Equal((1, 1, 0, 1, 0), surface.CallCounts);
    }

    [Theory]
    [MemberData(nameof(Injected))]
    public void InjectedFailure_AtEachCall_ReturnsIt(int call, int occurrence, VkResult code, ErrorKind kind, string message, int[] callsBefore)
    {
        FakeSurface surface = new() { Capabilities = Capabilities, Formats = _formats, Modes = _modes };
        VulkanFaultInjector faults = new();
        faults.FailNth((VulkanCall)call, occurrence, code);
        VulkanCallGate gate = new() { Faults = faults };

        Result<SurfaceFacts> result = SurfaceQueries.Read(surface, gate);

        Assert.Equal(new EngineError(kind, message, (int)code), result.Error);
        Assert.Equal((callsBefore[0], callsBefore[1], callsBefore[2], callsBefore[3], callsBefore[4]), surface.CallCounts);
        Assert.Equal(code == VkResult.ErrorDeviceLost, gate.IsLost);
    }

    [Fact]
    public void Capabilities_NativeFailure_ReturnsItBeforeTheLists()
    {
        FakeSurface surface = new() { CapabilitiesResult = VkResult.ErrorSurfaceLostKHR, Formats = _formats, Modes = _modes };

        Result<SurfaceFacts> result = SurfaceQueries.Read(surface, new VulkanCallGate());

        Assert.Equal(new EngineError(ErrorKind.Native, CapabilitiesFailed, (int)VkResult.ErrorSurfaceLostKHR), result.Error);
        Assert.Equal((1, 0, 0, 0, 0), surface.CallCounts);
    }

    [Fact]
    public void Read_Misuse_Throws()
    {
        Assert.Throws<ArgumentNullException>("gate", () => SurfaceQueries.Read(new FakeSurface(), null!));
        Assert.Throws<ArgumentNullException>("core", () => SurfaceQueries.Read(null!, new VkSurfaceKHR(1), new VulkanCallGate()));
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void RealSurface_ReadsFacts()
    {
        ValidationLog sink = new();
        (Logger logger, LogCategory category) = ValidationLog.Create(sink);
        using (logger)
        {
            RawSurface raw = new(category);
            try
            {
                List<GpuDevices.Adapter> adapters = raw.Presenting();
                Assert.NotEmpty(adapters);
                foreach (GpuDevices.Adapter adapter in adapters)
                {
                    using VulkanDeviceCore core = raw.CreateDevice(adapter, new VulkanCallGate());
                    Assert.Throws<ArgumentException>("surface", () => SurfaceQueries.Read(core, default, new VulkanCallGate()));

                    Result<SurfaceFacts> read = SurfaceQueries.Read(core, raw.Surface, new VulkanCallGate());

                    Assert.True(read.IsSuccess, read.ToString());
                    SurfaceFacts facts = read.Value;
                    VkSurfaceCapabilitiesKHR expected = RawCapabilities(adapter.Device, raw.Surface);
                    Assert.True(facts.MinImageCount >= 1, $"minImageCount {facts.MinImageCount}");
                    Assert.Equal((expected.MinImageCount, expected.MaxImageCount), (facts.MinImageCount, facts.MaxImageCount));
                    Assert.Equal((expected.CurrentExtent.Width, expected.CurrentExtent.Height), (facts.CurrentExtent.Width, facts.CurrentExtent.Height));
                    Assert.Equal(expected.CurrentTransform, facts.CurrentTransform);
                    Assert.True(System.Numerics.BitOperations.IsPow2((uint)facts.CurrentTransform), $"currentTransform {facts.CurrentTransform}");
                    Assert.NotEmpty(facts.Formats!);
                    Assert.Contains(VkPresentModeKHR.FifoKHR, facts.PresentModes!);
                    TestContext.Current.TestOutputHelper?.WriteLine(
                        $"{adapter.Candidate.Name}: family {adapter.Family}, images {facts.MinImageCount}-{facts.MaxImageCount}, transform {facts.CurrentTransform}, {facts.Formats!.Length} formats, modes [{string.Join(", ", facts.PresentModes!)}]");
                }
            }
            finally
            {
                raw.Dispose();
            }

            Assert.Equal(0, raw.Instance.ValidationErrors);
        }
    }

    private static SurfaceFacts ReadOk(FakeSurface surface)
    {
        Result<SurfaceFacts> result = SurfaceQueries.Read(surface, new VulkanCallGate());
        Assert.True(result.IsSuccess, result.ToString());
        return result.Value;
    }

    // The capabilities straight from the instance table, not through the code under test.
    private static VkSurfaceCapabilitiesKHR RawCapabilities(PhysicalDevice device, VkSurfaceKHR surface)
    {
        VkSurfaceCapabilitiesKHR capabilities = default;
        Assert.Equal(VkResult.Success, device.PInvoke.vkGetPhysicalDeviceSurfaceCapabilitiesKHR.Invoke(device.Handle, surface, &capabilities));
        return capabilities;
    }
}
