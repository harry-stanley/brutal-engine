using System;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Core.Memory;
using BrutalEngine.Graphics;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Platform;
using Xunit;

namespace BrutalEngine.TestUtilities.Tests;

/// <summary>
/// What each test of the fake needs in place of the front end: a script, a backend, a handle table and a tracking
/// allocator that must end with no live allocations. One fixture per test, never shared.
/// </summary>
internal sealed class FakeBackendFixture : IDisposable
{
    private readonly Logger _logger = new(new LoggerOptions { Sinks = [] });

    public FakeBackendFixture() => Backend = new FakeGraphicsBackend(Script);

    public TrackingAllocator Allocator { get; } = new(new HeapAllocator(), "fake-backend-tests");

    public FakeBackendScript Script { get; } = new();

    public FakeGraphicsBackend Backend { get; }

    public BackendObjectTable Objects { get; } = new();

    public static BufferDescription Buffer(long size) => new() { Size = size, Usage = BufferUsage.CopyDestination };

    public static TextureDescription Texture() =>
        new() { Format = TextureFormat.Rgba8Unorm, Width = 4, Height = 4, Usage = TextureUsage.Sampled };

    public BackendDeviceRequest Request(bool validation = false, int framesInFlight = 2, WindowSurfaceSource? compatible = null) =>
        new()
        {
            Logger = _logger,
            Allocator = Allocator,
            Validation = validation,
            FramesInFlight = framesInFlight,
            CompatibleSurface = compatible,
        };

    public FakeBackendDevice CreateDevice(bool validation = false, WindowSurfaceSource? compatible = null) =>
        Assert.IsType<FakeBackendDevice>(Backend.CreateDevice(Request(validation, compatible: compatible), 0, Objects).Value);

    /// <summary>A surface source for a new window of the given pixel size.</summary>
    public static WindowSurfaceSource Window(int width = 640, int height = 480, bool minimised = false) =>
        GraphicsInternals.NewSurfaceSource(
            new TestWindow { PixelSize = new WindowSize(width, height), State = minimised ? WindowState.Minimized : default },
            new TestSurfaceSource());

    /// <summary>Waits for a slot and opens its frame, as the front end does at the start of each frame.</summary>
    public static void OpenFrame(FakeBackendDevice device, int slot)
    {
        Assert.True(device.WaitForFrame(slot).IsSuccess);
        Assert.True(device.BeginFrame(slot).IsSuccess);
    }

    /// <summary>Acquires an image of a swapchain in an open frame and executes a plan that waits for it; returns its index.</summary>
    public static int ExecuteWithImage(FakeBackendDevice device, int slot, ulong swapchain)
    {
        BackendAcquire acquire = device.AcquireImage(swapchain, slot);
        Assert.Equal(SwapchainStatus.Success, acquire.Status);
        Assert.True(device.Execute(GraphicsInternals.NewFramePlan(slot, 0, [], [new(swapchain, acquire.ImageIndex)])).IsSuccess);
        return acquire.ImageIndex;
    }

    public static BackendSwapchainRequest Swapchain(ulong surface, WindowSurfaceSource source, ulong old = 0) =>
        new(surface, source, new SwapchainOptions(), old);

    /// <summary>Records a texture id in the table under a new handle, as the front end does, and returns a view of it.</summary>
    public TextureViewDescription ViewOf(ulong textureId, uint handleIndex)
    {
        var handle = new Handle(handleIndex, 1);
        GraphicsInternals.SetEntry(Objects, BackendObjectKind.Texture, handle, textureId);
        return new TextureViewDescription { Texture = GraphicsInternals.NewTextureHandle(handle) };
    }

    public void Dispose()
    {
        Backend.LastDevice?.Dispose();
        _logger.Dispose();
        Allocator.ThrowIfLeaks();
    }
}
