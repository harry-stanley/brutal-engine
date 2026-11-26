using System;
using BrutalEngine.Core.Logging;
using BrutalEngine.Core.Memory;
using BrutalEngine.Graphics;
using BrutalEngine.Graphics.Backend;
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

    public BackendDeviceRequest Request(bool validation = false, int framesInFlight = 2) =>
        new() { Logger = _logger, Allocator = Allocator, Validation = validation, FramesInFlight = framesInFlight };

    public FakeBackendDevice CreateDevice(bool validation = false) =>
        Assert.IsType<FakeBackendDevice>(Backend.CreateDevice(Request(validation), 0, Objects).Value);

    public void Dispose()
    {
        Backend.LastDevice?.Dispose();
        _logger.Dispose();
        Allocator.ThrowIfLeaks();
    }
}
