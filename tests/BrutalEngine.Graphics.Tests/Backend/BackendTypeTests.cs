using System;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using BrutalEngine.Graphics.Backend;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Backend;

/// <summary>
/// The backend interface's value types (docs/design/graphics.md, "The backend interface"): which are unmanaged, their
/// enum values, and their text forms.
/// </summary>
public sealed class BackendTypeTests
{
    public static TheoryData<string> UnmanagedTypes() =>
        [nameof(ResourceTransition), nameof(SwapchainImageUse), nameof(BackendAcquire), nameof(BackendPresent)];

    [Theory]
    [MemberData(nameof(UnmanagedTypes))]
    public void ValueTypes_AreUnmanaged(string type)
    {
        (Type clrType, bool containsReferences) = type switch
        {
            nameof(ResourceTransition) => (typeof(ResourceTransition), RuntimeHelpers.IsReferenceOrContainsReferences<ResourceTransition>()),
            nameof(SwapchainImageUse) => (typeof(SwapchainImageUse), RuntimeHelpers.IsReferenceOrContainsReferences<SwapchainImageUse>()),
            nameof(BackendAcquire) => (typeof(BackendAcquire), RuntimeHelpers.IsReferenceOrContainsReferences<BackendAcquire>()),
            nameof(BackendPresent) => (typeof(BackendPresent), RuntimeHelpers.IsReferenceOrContainsReferences<BackendPresent>()),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown type."),
        };

        Assert.True(clrType.IsValueType);
        Assert.False(containsReferences);
    }

    public static TheoryData<string> Enums() =>
        [nameof(BackendMemory), nameof(BackendObjectKind), nameof(SwapchainStatus), nameof(ResourceState)];

    [Theory]
    [MemberData(nameof(Enums))]
    public void EnumValues_MatchDesign(string type)
    {
        // Written from docs/design/graphics.md's backend interface block, in its order, numbered from 0.
        (Type enumType, (string Name, int Value)[] expected) = type switch
        {
            nameof(BackendMemory) => (
                typeof(BackendMemory),
                new[] { ("DeviceLocal", 0), ("DeviceLocalHostVisiblePreferred", 1), ("HostUpload", 2), ("HostReadback", 3) }),
            nameof(BackendObjectKind) => (
                typeof(BackendObjectKind),
                [
                    ("Buffer", 0), ("Texture", 1), ("TextureView", 2), ("Sampler", 3), ("ShaderModule", 4), ("BindGroupLayout", 5),
                    ("PipelineLayout", 6), ("BindGroup", 7), ("GraphicsPipeline", 8), ("ComputePipeline", 9),
                ]),
            nameof(SwapchainStatus) => (
                typeof(SwapchainStatus),
                [("Success", 0), ("Suboptimal", 1), ("OutOfDate", 2), ("SurfaceLost", 3), ("DeviceLost", 4)]),
            nameof(ResourceState) => (
                typeof(ResourceState),
                [
                    ("Undefined", 0), ("CopySource", 1), ("CopyDestination", 2), ("VertexBuffer", 3), ("IndexBuffer", 4),
                    ("IndirectBuffer", 5), ("UniformRead", 6), ("StorageRead", 7), ("StorageReadWrite", 8), ("SampledRead", 9),
                    ("ColorTarget", 10), ("DepthRead", 11), ("DepthReadWrite", 12), ("Present", 13),
                ]),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown enum."),
        };

        Assert.False(enumType.IsDefined(typeof(FlagsAttribute), inherit: false));
        Assert.Equal(typeof(int), Enum.GetUnderlyingType(enumType));
        Assert.Equal(expected, Enum.GetValuesAsUnderlyingType(enumType).Cast<int>().Select(v => (Enum.GetName(enumType, v)!, v)));
    }

    public static TheoryData<string> Records() =>
    [
        nameof(BackendBuffer), nameof(BackendSwapchainRequest), nameof(BackendSwapchain), nameof(BackendAcquire),
        nameof(BackendPresent), nameof(SwapchainImageUse),
    ];

    [Theory]
    [MemberData(nameof(Records))]
    public void ToString_UnderAnotherCulture_IsInvariant(string type)
    {
        (object value, string expected) = type switch
        {
            nameof(BackendBuffer) => ((object)new BackendBuffer(42, -7), "BackendBuffer { Id = 42, MappedPointer = -7 }"),
            nameof(BackendSwapchainRequest) => (
                new BackendSwapchainRequest(
                    7,
                    new WindowSurfaceSource(new FakeWindow(), new FakeVulkanSurfaceSource([], (nint)1)),
                    new SwapchainOptions { PresentMode = PresentMode.Mailbox, DebugName = "main" },
                    3),
                "BackendSwapchainRequest { Surface = 7, Source = BrutalEngine.Graphics.Backend.WindowSurfaceSource, "
                + "Options = SwapchainOptions { PresentMode = Mailbox, Format = Srgb, DebugName = main }, OldSwapchain = 3 }"),
            nameof(BackendSwapchain) => (
                new BackendSwapchain(9, new SwapchainInfo(-640, -480, TextureFormat.Bgra8UnormSrgb, PresentMode.Fifo, -2, SupportsCopyDestination: false)),
                "BackendSwapchain { Id = 9, Info = SwapchainInfo { Width = -640, Height = -480, Format = Bgra8UnormSrgb, PresentMode = Fifo, "
                + "ImageCount = -2, SupportsCopyDestination = False } }"),
            nameof(BackendAcquire) => (
                new BackendAcquire(SwapchainStatus.Suboptimal, -1, -1000001003),
                "BackendAcquire { Status = Suboptimal, ImageIndex = -1, NativeCode = -1000001003 }"),
            nameof(BackendPresent) => (
                new BackendPresent(SwapchainStatus.OutOfDate, -1000001004),
                "BackendPresent { Status = OutOfDate, NativeCode = -1000001004 }"),
            nameof(SwapchainImageUse) => (new SwapchainImageUse(3, -1), "SwapchainImageUse { Swapchain = 3, ImageIndex = -1 }"),
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
    public void BackendSwapchainRequest_ToString_Default_PrintsNothingForNullSource()
    {
        Assert.Equal(
            "BackendSwapchainRequest { Surface = 0, Source = , Options = , OldSwapchain = 0 }",
            default(BackendSwapchainRequest).ToString());
    }
}
