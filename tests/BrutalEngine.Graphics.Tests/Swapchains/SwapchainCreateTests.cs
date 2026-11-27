using System;
using System.Linq;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Tests.Backend;
using BrutalEngine.Graphics.Tests.Device;
using BrutalEngine.Platform;
using BrutalEngine.TestUtilities;
using Xunit;
using static BrutalEngine.Graphics.Tests.Swapchains.SwapchainTestKit;

namespace BrutalEngine.Graphics.Tests.Swapchains;

/// <summary>
/// <c>GraphicsDevice.CreateSwapchain</c>: the creation rules, the reused selection surface, a failed first creation, the
/// options and the registered images (docs/design/graphics.md section 3).
/// </summary>
public sealed class SwapchainCreateTests : IDisposable
{
    // The fake's ids come from one counter that starts at 1, and a device with a compatible window takes the first for
    // its selection surface.
    private const ulong SelectionSurface = 1;

    private readonly DeviceFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    public static TheoryData<PresentMode, SwapchainFormat> DefinedOptions()
    {
        var rows = new TheoryData<PresentMode, SwapchainFormat>();
        foreach (PresentMode mode in Enum.GetValues<PresentMode>())
        {
            foreach (SwapchainFormat format in Enum.GetValues<SwapchainFormat>())
            {
                rows.Add(mode, format);
            }
        }

        return rows;
    }

    [Theory]
    [InlineData("Surfaceless")]
    [InlineData("OffscreenOnly")]
    [InlineData("SecondForWindow")]
    [InlineData("CompatibleWindow")]
    [InlineData("OtherWindow")]
    [InlineData("Minimised")]
    [InlineData("ZeroSide")]
    [InlineData("SurfaceFails")]
    public void Rules(string rule)
    {
        FakeWindow compatible = Window();
        GraphicsDevice device = rule == "OffscreenOnly" ? _fixture.Create() : NewDevice(_fixture, compatible);
        FakeBackendDevice backend = _fixture.BackendDevice;
        FakeWindow other = Window();
        var options = new SwapchainOptions { DebugName = "main" };

        switch (rule)
        {
            case "Surfaceless":
                other.VulkanSurface = null;
                AssertFailure(device.CreateSwapchain(other, options), ErrorKind.Unsupported, "WindowSurface.None");
                Assert.Empty(Lifetime(backend));
                break;
            case "OffscreenOnly":
                AssertFailure(device.CreateSwapchain(other, options), ErrorKind.Unsupported, "offscreen-only");
                Assert.Empty(Lifetime(backend));
                break;
            case "SecondForWindow":
                SwapchainHandle first = device.CreateSwapchain(other, options).Value;
                InvalidOperationException second = Assert.Throws<InvalidOperationException>(() => device.CreateSwapchain(other, options));
                Assert.Contains("already has the live swapchain 'main'", second.Message, StringComparison.Ordinal);
                Assert.Equal(2, Lifetime(backend).Count);
                Assert.Equal(1, device.Statistics.LiveObjects);
                device.Destroy(first);
                break;
            case "CompatibleWindow":
                device.Destroy(device.CreateSwapchain(compatible, options).Value);
                Assert.Equal(
                    [(FakeBackendOperation.CreateSurface, SelectionSurface), (FakeBackendOperation.CreateSwapchain, SelectionSurface + 1)],
                    Lifetime(backend).Take(2));
                break;
            case "OtherWindow":
                SwapchainHandle created = device.CreateSwapchain(other, options).Value;
                ulong surface = LastId(backend, FakeBackendOperation.CreateSurface);
                Assert.NotEqual(SelectionSurface, surface);
                // The swapchain and its surface; the selection surface, never handed over, isn't counted.
                Assert.Equal(2, backend.LiveObjectCount);
                device.Destroy(created);
                break;
            case "Minimised":
                other.State = WindowState.Minimized;
                AssertFailure(device.CreateSwapchain(other, options), ErrorKind.Cancelled, "minimised");
                Assert.Empty(Lifetime(backend));
                break;
            case "ZeroSide":
                other.PixelSize = new WindowSize(640, 0);
                AssertFailure(device.CreateSwapchain(other, options), ErrorKind.Cancelled, "minimised");
                Assert.Empty(Lifetime(backend));
                break;
            case "SurfaceFails":
                _fixture.Script.FailNext(FakeBackendOperation.CreateSurface, new EngineError(ErrorKind.Unsupported, "no present support"));
                AssertFailure(device.CreateSwapchain(other, options), ErrorKind.Unsupported, "no present support");
                Assert.Equal([(FakeBackendOperation.CreateSurface, 0UL)], Lifetime(backend));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown rule.");
        }

        device.ThrowIfLeaks();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstCreateFails_DestroysSurface(bool compatibleWindow)
    {
        FakeWindow compatible = Window();
        GraphicsDevice device = NewDevice(_fixture, compatible);
        FakeBackendDevice backend = _fixture.BackendDevice;
        FakeWindow window = compatibleWindow ? compatible : Window();
        var lost = new EngineError(ErrorKind.NotFound, "surface lost", -1000000000);
        _fixture.Script.FailNext(FakeBackendOperation.CreateSwapchain, lost);

        Result<SwapchainHandle> failed = device.CreateSwapchain(window, new SwapchainOptions());

        // The surface made for it (for the compatible window, the selection surface) is destroyed with the failure.
        Assert.Equal(lost, failed.Error);
        ulong surface = LastId(backend, FakeBackendOperation.CreateSurface);
        Assert.Equal(compatibleWindow, surface == SelectionSurface);
        Assert.Equal(
            [(FakeBackendOperation.CreateSurface, surface), (FakeBackendOperation.CreateSwapchain, 0UL), (FakeBackendOperation.DestroySurface, surface)],
            Lifetime(backend));
        Assert.Equal(0, backend.LiveObjectCount);

        // The retry makes the window's only live surface, a new one.
        SwapchainHandle swapchain = device.CreateSwapchain(window, new SwapchainOptions()).Value;
        Assert.NotEqual(surface, LastId(backend, FakeBackendOperation.CreateSurface));
        Assert.Equal(2, backend.LiveObjectCount);
        device.Destroy(swapchain);
        device.ThrowIfLeaks();
    }

    [Fact]
    public void Create_ReportsInfoAndRegistersImagesTheSwapchainOwns()
    {
        GraphicsDevice device = NewDevice(_fixture, Window());
        var options = new SwapchainOptions { PresentMode = PresentMode.Mailbox, Format = SwapchainFormat.Unorm, DebugName = "main" };

        SwapchainHandle swapchain = device.CreateSwapchain(Window(320, 200), options).Value;

        // The fake's default info: the window's size, the format kind's BGRA format, the mode asked for and 3 images.
        Assert.Equal(new SwapchainInfo(320, 200, TextureFormat.Bgra8Unorm, PresentMode.Mailbox, 3, true), device.GetSwapchainInfo(swapchain));
        var images = Enumerable.Range(0, 3).Select(i => device.GetSwapchainImage(swapchain, i)).ToList();
        Assert.Equal(3, images.Select(image => image.Texture).Distinct().Count());
        foreach (SwapchainImage image in images)
        {
            Assert.Equal((320, 200, TextureFormat.Bgra8Unorm), (image.Width, image.Height, image.Format));
            Assert.Equal(image.View, device.GetDefaultView(image.Texture));
        }

        // Six image handles, none counted: the swapchain is the one live object.
        Assert.Equal(1, device.Statistics.LiveObjects);
        device.Destroy(swapchain);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(4, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 2)]
    [InlineData(int.MinValue, 0)]
    public void Options_OutOfRange_Throw(int presentMode, int format)
    {
        GraphicsDevice device = NewDevice(_fixture, Window());
        var options = new SwapchainOptions { PresentMode = (PresentMode)presentMode, Format = (SwapchainFormat)format };

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => device.CreateSwapchain(Window(), options));
        Assert.Empty(Lifetime(_fixture.BackendDevice));
    }

    [Theory]
    [MemberData(nameof(DefinedOptions))]
    public void Options_EveryDefinedValue_Accepted(PresentMode presentMode, SwapchainFormat format)
    {
        GraphicsDevice device = NewDevice(_fixture, Window());

        Result<SwapchainHandle> created = device.CreateSwapchain(Window(), new SwapchainOptions { PresentMode = presentMode, Format = format });

        Assert.Equal(presentMode, device.GetSwapchainInfo(created.Value).PresentMode);
        device.Destroy(created.Value);
    }

    [Fact]
    public void Arguments_Null_Throw()
    {
        GraphicsDevice device = NewDevice(_fixture, Window());

        _ = Assert.Throws<ArgumentNullException>(() => device.CreateSwapchain(null!, new SwapchainOptions()));
        _ = Assert.Throws<ArgumentNullException>(() => device.CreateSwapchain(Window(), null!));
        Assert.Empty(Lifetime(_fixture.BackendDevice));
    }

    [Fact]
    public void Handles_NullOrStale_Throw()
    {
        GraphicsDevice device = NewDevice(_fixture, Window());
        SwapchainHandle swapchain = device.CreateSwapchain(Window(), new SwapchainOptions()).Value;
        device.Destroy(swapchain);

        _ = Assert.Throws<ArgumentException>(() => device.GetSwapchainInfo(default));
        _ = Assert.Throws<ArgumentException>(() => device.Destroy(default(SwapchainHandle)));
        _ = Assert.Throws<StaleHandleException>(() => device.GetSwapchainInfo(swapchain));
        _ = Assert.Throws<StaleHandleException>(() => device.Destroy(swapchain));
    }

    [Fact]
    public void Disposed_Throws()
    {
        FakeWindow compatible = Window();
        GraphicsDevice device = NewDevice(_fixture, compatible);
        SwapchainHandle swapchain = device.CreateSwapchain(Window(), new SwapchainOptions()).Value;
        device.Dispose();

        _ = Assert.Throws<ObjectDisposedException>(() => device.CreateSwapchain(compatible, new SwapchainOptions()));
        _ = Assert.Throws<ObjectDisposedException>(() => device.GetSwapchainInfo(swapchain));
        _ = Assert.Throws<ObjectDisposedException>(() => device.Destroy(swapchain));
    }

    private static void AssertFailure(Result<SwapchainHandle> result, ErrorKind kind, string text)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(kind, result.Error.Kind);
        Assert.Contains(text, result.Error.Message, StringComparison.Ordinal);
    }
}
