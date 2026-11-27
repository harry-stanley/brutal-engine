using System;
using System.Linq;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Tests.Backend;
using BrutalEngine.Graphics.Tests.Device;
using BrutalEngine.TestUtilities;
using Xunit;
using static BrutalEngine.Graphics.Tests.Swapchains.SwapchainTestKit;

namespace BrutalEngine.Graphics.Tests.Swapchains;

/// <summary>
/// <c>GraphicsDevice.Destroy(SwapchainHandle)</c>, leftovers at <c>Dispose</c>, the leak accounting and the images the
/// swapchain owns (docs/design/graphics.md sections 1, 3 and 13).
/// </summary>
public sealed class SwapchainDestroyTests : IDisposable
{
    private readonly DeviceFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void Order_SwapchainThenSurface(int framesInFlight, bool inOpenFrame)
    {
        FakeWindow compatible = Window();
        GraphicsDevice device = NewDevice(_fixture, compatible, framesInFlight);
        FakeBackendDevice backend = _fixture.BackendDevice;
        SwapchainHandle swapchain = device.CreateSwapchain(compatible, new SwapchainOptions()).Value;
        ulong surface = LastId(backend, FakeBackendOperation.CreateSurface);
        ulong id = LastId(backend, FakeBackendOperation.CreateSwapchain);

        // Frame 0 may use the swapchain: destroyed in it, or right after it was submitted.
        _ = device.BeginFrame(out CommandList commands);
        if (!inOpenFrame)
        {
            _ = device.SubmitFrame(commands);
        }

        device.Destroy(swapchain);
        _ = Assert.Throws<StaleHandleException>(() => device.GetSwapchainInfo(swapchain));
        Assert.Equal(0, device.Statistics.LiveObjects);
        if (inOpenFrame)
        {
            _ = device.SubmitFrame(commands);
        }

        // Frame 0's slot is waited for again when frame framesInFlight begins: only then do both go, swapchain first.
        for (int frame = 1; frame <= framesInFlight; frame++)
        {
            Assert.DoesNotContain(Lifetime(backend), call => call.Operation == FakeBackendOperation.DestroySwapchain);
            _ = device.BeginFrame(out commands);
            _ = device.SubmitFrame(commands);
        }

        Assert.Equal([(FakeBackendOperation.DestroySwapchain, id), (FakeBackendOperation.DestroySurface, surface)], Lifetime(backend).Skip(2));
        Assert.Equal(0, backend.LiveObjectCount);
    }

    [Fact]
    public void Order_BeforeFirstFrame_DestroysAtOnce()
    {
        GraphicsDevice device = NewDevice(_fixture, Window());
        FakeBackendDevice backend = _fixture.BackendDevice;
        SwapchainHandle swapchain = device.CreateSwapchain(Window(), new SwapchainOptions()).Value;
        ulong surface = LastId(backend, FakeBackendOperation.CreateSurface);
        ulong id = LastId(backend, FakeBackendOperation.CreateSwapchain);

        device.Destroy(swapchain);

        Assert.Equal([(FakeBackendOperation.DestroySwapchain, id), (FakeBackendOperation.DestroySurface, surface)], Lifetime(backend).Skip(2));
    }

    [Fact]
    public void Leftovers_DestroyedAtDisposeAndReported()
    {
        FakeWindow compatible = Window();
        GraphicsDevice device = NewDevice(_fixture, compatible);
        FakeBackendDevice backend = _fixture.BackendDevice;
        _ = device.CreateSwapchain(compatible, new SwapchainOptions { DebugName = "main" }).Value;
        (ulong mainId, ulong mainSurface) = (LastId(backend, FakeBackendOperation.CreateSwapchain), LastId(backend, FakeBackendOperation.CreateSurface));
        BufferHandle buffer = device.CreateBuffer(new BufferDescription { Size = 64, Usage = BufferUsage.Vertex, DebugName = "between" }).Value;
        _ = device.CreateSwapchain(Window(), new SwapchainOptions { DebugName = "second" }).Value;
        (ulong secondId, ulong secondSurface) = (LastId(backend, FakeBackendOperation.CreateSwapchain), LastId(backend, FakeBackendOperation.CreateSurface));
        ulong bufferId = backend.Objects.GetId(buffer);

        // Two swapchains and a buffer: their six images each never count.
        Assert.Equal(3, device.Statistics.LiveObjects);
        GpuResourceLeakException leaks = Assert.Throws<GpuResourceLeakException>(device.ThrowIfLeaks);
        string[] expected = ["Swapchain 'main'", "Buffer 'between'", "Swapchain 'second'"];
        Assert.Equal(expected, leaks.LeakedObjects);

        device.Dispose();

        // Logged oldest first; destroyed newest first, each swapchain before its surface, the swapchains before the buffer.
        Assert.Equal(
            expected.Select(line => $"Leak: {line} was still alive when the device was disposed."),
            _fixture.Entries().Where(entry => entry.Level == LogLevel.Error && entry.Category == "Graphics").Select(entry => entry.Message));
        Assert.Equal(
            [
                (FakeBackendOperation.DestroySwapchain, secondId), (FakeBackendOperation.DestroySurface, secondSurface),
                (FakeBackendOperation.DestroySwapchain, mainId), (FakeBackendOperation.DestroySurface, mainSurface),
            ],
            Lifetime(backend).Skip(4));
        int destroyed = backend.Calls.ToList().FindIndex(call => call.Operation == FakeBackendOperation.Destroy && call.Id == bufferId);
        int lastSurface = backend.Calls.ToList().FindLastIndex(call => call.Operation == FakeBackendOperation.DestroySurface);
        Assert.True(destroyed > lastSurface, $"The buffer was destroyed at call {destroyed}, before the last surface at {lastSurface}.");
    }

    [Fact]
    public void SwapchainImages_RejectedByDestroy()
    {
        GraphicsDevice device = NewDevice(_fixture, Window());
        FakeBackendDevice backend = _fixture.BackendDevice;
        SwapchainHandle swapchain = device.CreateSwapchain(Window(), new SwapchainOptions { DebugName = "main" }).Value;
        // Not image 0: the second image's handles are neither the first issued nor index 0.
        SwapchainImage image = device.GetSwapchainImage(swapchain, 1);
        int calls = backend.Calls.Count;

        InvalidOperationException texture = Assert.Throws<InvalidOperationException>(() => device.Destroy(image.Texture));
        InvalidOperationException view = Assert.Throws<InvalidOperationException>(() => device.Destroy(image.View));
        InvalidOperationException extraView = Assert.Throws<InvalidOperationException>(
            () => device.CreateTextureView(new TextureViewDescription { Texture = image.Texture }));

        foreach (InvalidOperationException rejected in new[] { texture, view, extraView })
        {
            Assert.Contains("'main' is a swapchain image", rejected.Message, StringComparison.Ordinal);
        }

        // Nothing reached the backend; the image is still live and still not counted.
        Assert.Equal(calls, backend.Calls.Count);
        Assert.Equal(image.View, device.GetDefaultView(image.Texture));
        Assert.Equal(["Swapchain 'main'"], Assert.Throws<GpuResourceLeakException>(device.ThrowIfLeaks).LeakedObjects);

        // Destroying the swapchain ends its images' handles too, and the fake accepts its images going with it.
        device.Destroy(swapchain);
        _ = Assert.Throws<StaleHandleException>(() => device.GetDefaultView(image.Texture));
        _ = Assert.Throws<StaleHandleException>(() => device.Destroy(image.View));
        Assert.Equal(0, device.Statistics.LiveObjects);
        device.ThrowIfLeaks();
    }

    [Fact]
    public void Recreate_AfterDestroyInSubmittedFrame_WaitsForOldSwapchain()
    {
        GraphicsDevice device = NewDevice(_fixture, Window());
        FakeBackendDevice backend = _fixture.BackendDevice;
        FakeWindow window = Window();
        SwapchainHandle old = device.CreateSwapchain(window, new SwapchainOptions()).Value;
        (ulong oldId, ulong oldSurface) = (LastId(backend, FakeBackendOperation.CreateSwapchain), LastId(backend, FakeBackendOperation.CreateSurface));
        _ = device.BeginFrame(out CommandList commands);
        _ = device.SubmitFrame(commands);
        device.Destroy(old);

        SwapchainHandle created = device.CreateSwapchain(window, new SwapchainOptions()).Value;

        // The old swapchain and surface are gone before the window gets its new surface.
        Assert.Equal(
            [
                (FakeBackendOperation.WaitIdle, 0UL), (FakeBackendOperation.DestroySwapchain, oldId), (FakeBackendOperation.DestroySurface, oldSurface),
                (FakeBackendOperation.CreateSurface, LastId(backend, FakeBackendOperation.CreateSurface)),
                (FakeBackendOperation.CreateSwapchain, LastId(backend, FakeBackendOperation.CreateSwapchain)),
            ],
            Lifetime(backend).Skip(2));
        device.Destroy(created);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Recreate_AfterDestroyBeforeItsFrameIsSubmitted_Throws(bool inOpenFrame)
    {
        GraphicsDevice device = NewDevice(_fixture, Window());
        FakeBackendDevice backend = _fixture.BackendDevice;
        FakeWindow window = Window();
        SwapchainHandle old = device.CreateSwapchain(window, new SwapchainOptions()).Value;
        _ = device.BeginFrame(out CommandList commands);
        BufferHandle target = default;
        if (!inOpenFrame)
        {
            // A pending write between frames: the old swapchain waits for the next frame, which doesn't exist yet.
            _ = device.SubmitFrame(commands);
            target = device.CreateBuffer(new BufferDescription { Size = 16, Usage = BufferUsage.CopyDestination }).Value;
            Assert.True(device.WriteBuffer(target, 0, new byte[16]).IsSuccess);
        }

        device.Destroy(old);
        int calls = Lifetime(backend).Count;

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => device.CreateSwapchain(window, new SwapchainOptions()));

        Assert.Contains("hasn't been submitted", error.Message, StringComparison.Ordinal);
        Assert.Equal(calls, Lifetime(backend).Count);
        if (!inOpenFrame)
        {
            _ = device.BeginFrame(out commands);
        }

        _ = device.SubmitFrame(commands);
        SwapchainHandle created = device.CreateSwapchain(window, new SwapchainOptions()).Value;
        Assert.Equal(FakeBackendOperation.WaitIdle, Lifetime(backend)[calls].Operation);
        device.Destroy(created);
        if (!target.IsNull)
        {
            device.Destroy(target);
        }
    }

    [Fact]
    public void Recreate_AfterOldSwapchainRetired_OrForAnotherWindow_DoesNotWait()
    {
        GraphicsDevice device = NewDevice(_fixture, Window());
        FakeBackendDevice backend = _fixture.BackendDevice;
        FakeWindow window = Window();
        FakeWindow other = Window();
        _ = device.BeginFrame(out CommandList commands);
        _ = device.SubmitFrame(commands);
        device.Destroy(device.CreateSwapchain(window, new SwapchainOptions()).Value);

        // Another window doesn't wait for this one's old swapchain.
        SwapchainHandle forOther = device.CreateSwapchain(other, new SwapchainOptions()).Value;
        Assert.DoesNotContain(Lifetime(backend), call => call.Operation == FakeBackendOperation.WaitIdle);

        // Two more frames retire frame 0's queue: the window's next swapchain needn't wait either.
        for (int frame = 1; frame <= 2; frame++)
        {
            _ = device.BeginFrame(out commands);
            _ = device.SubmitFrame(commands);
        }

        SwapchainHandle again = device.CreateSwapchain(window, new SwapchainOptions()).Value;
        Assert.DoesNotContain(Lifetime(backend), call => call.Operation == FakeBackendOperation.WaitIdle);
        device.Destroy(again);
        device.Destroy(forOther);
    }

    [Fact]
    public void Recreate_WaitFails_ReturnsTheFailure()
    {
        GraphicsDevice device = NewDevice(_fixture, Window());
        FakeWindow window = Window();
        SwapchainHandle old = device.CreateSwapchain(window, new SwapchainOptions()).Value;
        _ = device.BeginFrame(out CommandList commands);
        _ = device.SubmitFrame(commands);
        device.Destroy(old);
        var lost = new EngineError(ErrorKind.Unavailable, "device lost", -4);
        _fixture.Script.FailNext(FakeBackendOperation.WaitIdle, lost);
        int calls = Lifetime(_fixture.BackendDevice).Count;

        Result<SwapchainHandle> created = device.CreateSwapchain(window, new SwapchainOptions());

        // The failed wait is the only new call: no surface is made while the old one may still be alive.
        Assert.Equal(lost, created.Error);
        Assert.Equal([(FakeBackendOperation.WaitIdle, 0UL)], Lifetime(_fixture.BackendDevice).Skip(calls));
        Assert.True(device.IsLost);
    }
}
