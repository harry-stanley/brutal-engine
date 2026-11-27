using System;
using System.Collections.Generic;
using System.Linq;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Tests.Backend;
using BrutalEngine.Graphics.Tests.Device;
using BrutalEngine.Platform;
using BrutalEngine.TestUtilities;
using Xunit;
using static BrutalEngine.Graphics.Tests.Swapchains.SwapchainTestKit;

namespace BrutalEngine.Graphics.Tests.Swapchains;

/// <summary>
/// <c>CommandList.AcquireSwapchainImage</c>: the status table, the M0 recreation rules and failed recreations
/// (docs/design/graphics.md section 3 and "The backend interface").
/// </summary>
public sealed class SwapchainAcquireTests : IDisposable
{
    private const int OutOfDateCode = -1000001004;

    private readonly DeviceFixture _fixture = new();
    private readonly List<BackendSwapchainRequest> _requests = [];
    private readonly FakeWindow _window = Window();
    private GraphicsDevice _device = null!;
    private SwapchainHandle _swapchain;

    public SwapchainAcquireTests() => _fixture.Script.SwapchainInfo = request =>
    {
        _requests.Add(request);
        return FakeBackendScript.DefaultSwapchainInfo(request);
    };

    public void Dispose() => _fixture.Dispose();

    [Theory]
    [InlineData(SwapchainStatus.Success, AcquireStatus.Acquired, AcquireStatus.Acquired)]
    [InlineData(SwapchainStatus.Suboptimal, AcquireStatus.Acquired, AcquireStatus.Recreated)]
    [InlineData(SwapchainStatus.OutOfDate, AcquireStatus.Recreated, AcquireStatus.Acquired)]
    [InlineData(SwapchainStatus.SurfaceLost, AcquireStatus.SurfaceLost, AcquireStatus.SurfaceLost)]
    [InlineData(SwapchainStatus.DeviceLost, AcquireStatus.DeviceLost, AcquireStatus.DeviceLost)]
    public void StatusTable(SwapchainStatus backend, AcquireStatus expected, AcquireStatus next)
    {
        Setup();
        _fixture.Script.SwapchainStatusNext(FakeBackendOperation.AcquireImage, backend, -7);

        (AcquireStatus status, SwapchainImage image) = Frame();

        Assert.Equal(expected, status);
        bool hasImage = expected is AcquireStatus.Acquired or AcquireStatus.Recreated;
        Assert.Equal(hasImage ? _device.GetSwapchainImage(_swapchain, 0) : default, image);
        Assert.Equal(expected == AcquireStatus.Recreated ? 2 : 1, Creates());
        Assert.Equal(backend == SwapchainStatus.DeviceLost, _device.IsLost);
        if (backend == SwapchainStatus.DeviceLost)
        {
            Assert.Equal(("AcquireSwapchainImage", -7), (_device.LostOperation, _device.LostNativeCode));
        }

        // The next frame: a suboptimal acquire recreates now; a lost surface or device stays lost, without an acquire.
        (int creates, int acquires) = (Creates(), Acquires());
        Assert.Equal(next, Frame().Status);
        Assert.Equal(next == AcquireStatus.Recreated ? creates + 1 : creates, Creates());
        Assert.Equal(next is AcquireStatus.SurfaceLost or AcquireStatus.DeviceLost ? acquires : acquires + 1, Acquires());
    }

    [Fact]
    public void OutOfDateAfterRecreation_Skipped()
    {
        Setup();
        // The recreation's creation arms a second out-of-date acquire, so the one retry fails too.
        _fixture.Script.SwapchainInfo = request =>
        {
            _fixture.Script.SwapchainStatusNext(FakeBackendOperation.AcquireImage, SwapchainStatus.OutOfDate, OutOfDateCode);
            return FakeBackendScript.DefaultSwapchainInfo(request);
        };
        _fixture.Script.SwapchainStatusNext(FakeBackendOperation.AcquireImage, SwapchainStatus.OutOfDate, OutOfDateCode);

        (AcquireStatus status, SwapchainImage image) = Frame();

        Assert.Equal((AcquireStatus.Skipped, default(SwapchainImage)), (status, image));
        Assert.Equal((2, 2), (Creates(), Acquires()));
        Assert.False(_device.IsLost);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NoSizeChange_NoRecreation(int framesInFlight)
    {
        Setup(framesInFlight);
        ulong first = LastId(_fixture.BackendDevice, FakeBackendOperation.CreateSwapchain);
        for (int frame = 0; frame < 5; frame++)
        {
            Assert.Equal(AcquireStatus.Acquired, Frame().Status);
        }

        // A size change recreates once, from the old swapchain, after waiting for the GPU; then it is steady again.
        _window.PixelSize = new WindowSize(800, 600);
        (AcquireStatus status, SwapchainImage image) = Frame();
        Assert.Equal((AcquireStatus.Recreated, 800, 600), (status, image.Width, image.Height));
        Assert.Equal(new SwapchainInfo(800, 600, TextureFormat.Bgra8UnormSrgb, PresentMode.Fifo, 3, true), _device.GetSwapchainInfo(_swapchain));
        Assert.Equal([0UL, first], _requests.Select(request => request.OldSwapchain));
        ulong second = LastId(_fixture.BackendDevice, FakeBackendOperation.CreateSwapchain);
        Assert.Equal(
            [(FakeBackendOperation.WaitIdle, 0UL), (FakeBackendOperation.CreateSwapchain, second), (FakeBackendOperation.DestroySwapchain, first)],
            Lifetime(_fixture.BackendDevice).Skip(2));
        for (int frame = 0; frame < 3; frame++)
        {
            Assert.Equal(AcquireStatus.Acquired, Frame().Status);
        }

        // Minimised or a zero side: no acquire and no recreation; restored at the same size, no recreation either.
        int acquires = Acquires();
        _window.State = WindowState.Minimized;
        Assert.Equal((AcquireStatus.Minimised, default(SwapchainImage)), Frame());
        _window.State = default;
        _window.PixelSize = new WindowSize(800, 0);
        Assert.Equal(AcquireStatus.Minimised, Frame().Status);
        _window.PixelSize = new WindowSize(800, 600);
        Assert.Equal(AcquireStatus.Acquired, Frame().Status);
        Assert.Equal((2, acquires + 1), (Creates(), Acquires()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NoSizeChange_ClampedExtent_NoRecreation(int framesInFlight)
    {
        // The surface clamps the extent below the window's size: the swapchain is compared with the size it was created
        // for, so the smaller extent alone never recreates it.
        _fixture.Script.SwapchainInfo = request =>
        {
            _requests.Add(request);
            return FakeBackendScript.DefaultSwapchainInfo(request) with { Width = request.Source.PixelWidth - 40, Height = request.Source.PixelHeight - 80 };
        };
        Setup(framesInFlight);
        for (int frame = 0; frame < 5; frame++)
        {
            (AcquireStatus status, SwapchainImage image) = Frame();
            Assert.Equal((AcquireStatus.Acquired, 600, 400), (status, image.Width, image.Height));
        }

        Assert.Equal(1, Creates());

        // A real size change still recreates once, then the new clamped extent is steady too.
        _window.PixelSize = new WindowSize(800, 600);
        (AcquireStatus resized, SwapchainImage clamped) = Frame();
        Assert.Equal((AcquireStatus.Recreated, 760, 520), (resized, clamped.Width, clamped.Height));
        for (int frame = 0; frame < 3; frame++)
        {
            Assert.Equal(AcquireStatus.Acquired, Frame().Status);
        }

        Assert.Equal(2, Creates());
    }

    [Theory]
    [InlineData(ErrorKind.Unavailable, AcquireStatus.DeviceLost)]
    [InlineData(ErrorKind.NotFound, AcquireStatus.SurfaceLost)]
    [InlineData(ErrorKind.Cancelled, AcquireStatus.Minimised)]
    [InlineData(ErrorKind.OutOfMemory, AcquireStatus.Skipped)]
    [InlineData(ErrorKind.Native, AcquireStatus.Skipped)]
    public void FailedRecreation_Mapping(ErrorKind kind, AcquireStatus expected)
    {
        Setup();
        ulong old = LastId(_fixture.BackendDevice, FakeBackendOperation.CreateSwapchain);
        SwapchainImage oldImage = _device.GetSwapchainImage(_swapchain, 0);
        _window.PixelSize = new WindowSize(320, 200);
        _fixture.Script.FailNext(FakeBackendOperation.CreateSwapchain, new EngineError(kind, "recreation failed", -9));

        (AcquireStatus status, SwapchainImage image) = Frame();

        Assert.Equal((expected, default(SwapchainImage)), (status, image));
        // The old swapchain went whatever the result; the handle keeps its surface and stays live.
        Assert.Equal((FakeBackendOperation.DestroySwapchain, old), Lifetime(_fixture.BackendDevice)[^1]);
        _ = Assert.Throws<StaleHandleException>(() => _device.GetDefaultView(oldImage.Texture));
        Assert.Equal(expected == AcquireStatus.DeviceLost, _device.IsLost);
        Assert.Equal(1, _device.Statistics.LiveObjects);
        bool logged = _fixture.Entries().Any(entry => entry.Level == LogLevel.Error && entry.Message.Contains("recreation failed", StringComparison.Ordinal));
        Assert.Equal(expected is AcquireStatus.Skipped or AcquireStatus.DeviceLost, logged);
    }

    [Theory]
    [InlineData(ErrorKind.OutOfMemory)]
    [InlineData(ErrorKind.Cancelled)]
    public void AfterFailedRecreation_NextCreateHasNoOldSwapchain(ErrorKind kind)
    {
        Setup();
        ulong old = LastId(_fixture.BackendDevice, FakeBackendOperation.CreateSwapchain);
        _window.PixelSize = new WindowSize(320, 200);
        _fixture.Script.FailNext(FakeBackendOperation.CreateSwapchain, new EngineError(kind, "recreation failed", -9));
        Assert.NotEqual(AcquireStatus.Acquired, Frame().Status);

        // No size change since: the missing swapchain alone makes the next acquire create one, from nothing.
        (AcquireStatus status, SwapchainImage image) = Frame();

        Assert.Equal((AcquireStatus.Recreated, 320), (status, image.Width));
        Assert.Equal([0UL, old, 0UL], _requests.Select(request => request.OldSwapchain));
        Assert.Equal(AcquireStatus.Acquired, Frame().Status);
    }

    [Fact]
    public void Recreation_WaitIdleFails_DeviceLost()
    {
        Setup();
        _window.PixelSize = new WindowSize(320, 200);
        _fixture.Script.FailNext(FakeBackendOperation.WaitIdle, new EngineError(ErrorKind.Timeout, "hung", -3));

        Assert.Equal(AcquireStatus.DeviceLost, Frame().Status);

        Assert.Equal(1, Creates());
        Assert.True(_device.IsLost);
    }

    [Fact]
    public void Recreation_TablesCannotGrow_DestroysNewSwapchain()
    {
        Setup();
        ulong old = LastId(_fixture.BackendDevice, FakeBackendOperation.CreateSwapchain);
        // The recreated swapchain has more images than the texture table holds, and the table can't grow.
        _fixture.Script.SwapchainInfo = request =>
        {
            _requests.Add(request);
            return FakeBackendScript.DefaultSwapchainInfo(request) with { ImageCount = BackendObjectTable.InitialCapacity + 4 };
        };
        _fixture.BackendDevice.Objects.BeforeGrowth = kind =>
        {
            if (kind == BackendObjectKind.Texture)
            {
                throw new InsufficientMemoryException("Simulated: the texture table can't grow.");
            }
        };
        _window.PixelSize = new WindowSize(320, 200);
        _ = _device.BeginFrame(out CommandList commands);

        _ = Assert.Throws<InsufficientMemoryException>(() => commands.AcquireSwapchainImage(_swapchain, out _));

        // The new swapchain went at once and the old one whatever the result; the handle is left without images.
        ulong created = LastId(_fixture.BackendDevice, FakeBackendOperation.CreateSwapchain);
        Assert.Equal(
            [(FakeBackendOperation.WaitIdle, 0UL), (FakeBackendOperation.CreateSwapchain, created), (FakeBackendOperation.DestroySwapchain, old), (FakeBackendOperation.DestroySwapchain, created)],
            Lifetime(_fixture.BackendDevice).Skip(2));
        Assert.Equal(1, _device.Statistics.LiveObjects);
        _ = _device.SubmitFrame(commands);

        // Once the table can grow, the next acquire creates the swapchain again, from nothing.
        _fixture.BackendDevice.Objects.BeforeGrowth = null;
        (AcquireStatus status, SwapchainImage image) = Frame();
        Assert.Equal((AcquireStatus.Recreated, 320), (status, image.Width));
        Assert.Equal([0UL, old, 0UL], _requests.Select(request => request.OldSwapchain));
    }

    [Fact]
    public void Misuse_Throws()
    {
        Setup();
        _ = _device.BeginFrame(out CommandList commands);
        int calls = _fixture.BackendDevice.Calls.Count;

        // Off the owner thread: refused before the window is read or the backend is called.
        _ = Assert.IsType<WrongThreadException>(GraphicsDeviceThreadTests.OnOtherThread(() => commands.AcquireSwapchainImage(_swapchain, out _)));
        Assert.Equal(calls, _fixture.BackendDevice.Calls.Count);

        Assert.Equal(AcquireStatus.Acquired, commands.AcquireSwapchainImage(_swapchain, out _));
        int acquires = Acquires();

        _ = Assert.Throws<InvalidOperationException>(() => commands.AcquireSwapchainImage(_swapchain, out _));
        _ = Assert.Throws<InvalidOperationException>(() => default(CommandList).AcquireSwapchainImage(_swapchain, out _));
        _ = Assert.Throws<ArgumentException>(() => commands.AcquireSwapchainImage(default, out _));
        _ = Assert.Throws<StaleHandleException>(() => commands.AcquireSwapchainImage(new SwapchainHandle(new Handle(5, 1)), out _));
        Assert.Equal(acquires, Acquires());
        _ = _device.SubmitFrame(commands);
        _ = Assert.Throws<InvalidOperationException>(() => commands.AcquireSwapchainImage(_swapchain, out _));

        // After Dispose, the open frame's list too.
        _ = _device.BeginFrame(out commands);
        _device.Dispose();
        calls = _fixture.BackendDevice.Calls.Count;
        _ = Assert.Throws<ObjectDisposedException>(() => commands.AcquireSwapchainImage(_swapchain, out _));
        Assert.Equal(calls, _fixture.BackendDevice.Calls.Count);
    }

    private void Setup(int framesInFlight = 2)
    {
        _device = NewDevice(_fixture, _window, framesInFlight);
        _swapchain = _device.CreateSwapchain(_window, new SwapchainOptions { DebugName = "main" }).Value;
    }

    // One frame that acquires the swapchain and submits.
    private (AcquireStatus Status, SwapchainImage Image) Frame()
    {
        _ = _device.BeginFrame(out CommandList commands);
        AcquireStatus status = commands.AcquireSwapchainImage(_swapchain, out SwapchainImage image);
        _ = _device.SubmitFrame(commands);
        return (status, image);
    }

    private int Creates() => _fixture.BackendDevice.Calls.Count(call => call.Operation == FakeBackendOperation.CreateSwapchain);

    private int Acquires() => _fixture.BackendDevice.Calls.Count(call => call.Operation == FakeBackendOperation.AcquireImage);
}
