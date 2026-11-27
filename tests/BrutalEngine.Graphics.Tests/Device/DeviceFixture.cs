using System;
using System.Collections.Generic;
using System.Text;
using BrutalEngine.Core.Logging;
using BrutalEngine.Core.Logging.Sinks;
using BrutalEngine.Core.Memory;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.TestUtilities;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Device;

/// <summary>
/// What each device test needs: a fake backend with its script, a logger that keeps its entries, and a tracking
/// allocator that must end with no live allocations. One fixture per test, never shared. <see cref="Dispose"/> disposes
/// every device either <c>Create</c> overload made (on the calling thread, which must be the test's), then checks for leaks.
/// </summary>
internal sealed class DeviceFixture : IDisposable
{
    private readonly List<GraphicsDevice> _devices = [];

    public DeviceFixture()
    {
        Logger = new Logger(new LoggerOptions { Sinks = [Sink] });
        Backend = new FakeGraphicsBackend(Script);
    }

    public TrackingAllocator Allocator { get; } = new(new HeapAllocator(), "graphics-device-tests");

    // Room for every leak line a property test's device can log at Dispose, so no entry is dropped.
    public MemoryLogSink Sink { get; } = new(1024, 256 * 1024);

    public Logger Logger { get; }

    public FakeBackendScript Script { get; } = new();

    public FakeGraphicsBackend Backend { get; }

    /// <summary>The device the fake backend created last; fails the test when there is none.</summary>
    public FakeBackendDevice BackendDevice => Backend.LastDevice ?? throw new InvalidOperationException("No backend device was created.");

    /// <summary>Options with this fixture's logger and allocator, and every other setting at its default.</summary>
    public GraphicsDeviceOptions Options() => new() { Logger = Logger, Allocator = Allocator };

    /// <summary>Creates a device on the fake backend, asserting success; the fixture disposes it.</summary>
    public GraphicsDevice Create(GraphicsDeviceOptions? options = null) => Create(Backend, options);

    /// <summary>Creates a device on <paramref name="backend"/> (a wrapper over this fixture's fake), asserting success; the fixture disposes it.</summary>
    public GraphicsDevice Create(IGraphicsBackend backend, GraphicsDeviceOptions? options = null)
    {
        GraphicsDevice device = GraphicsDevice.Create(backend, options ?? Options()).Value;
        _devices.Add(device);
        return device;
    }

    /// <summary>Every entry the logger wrote so far, oldest first, with its message decoded from UTF-8.</summary>
    public List<(LogLevel Level, string Category, string Message)> Entries()
    {
        Logger.Flush();
        var records = new MemoryLogRecord[Sink.CapacityEntries];
        byte[] messages = new byte[Sink.CapacityBytes];
        int count = Sink.CopyTo(records, messages);
        var entries = new List<(LogLevel, string, string)>(count);
        for (int i = 0; i < count; i++)
        {
            entries.Add((records[i].Level, records[i].Category, Encoding.UTF8.GetString(records[i].Message.Span)));
        }

        return entries;
    }

    public void Dispose()
    {
        foreach (GraphicsDevice device in _devices)
        {
            device.Dispose();
        }

        Logger.Dispose();
        Allocator.ThrowIfLeaks();
        Assert.Equal(0, Sink.DroppedCount);
    }
}
