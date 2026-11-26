using System;
using System.Threading;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Device;

/// <summary>The device's owner-thread rule (docs/design/graphics.md section 14).</summary>
public sealed class GraphicsDeviceThreadTests : IDisposable
{
    private static readonly TimeSpan _joinTimeout = TimeSpan.FromSeconds(30);

    private readonly DeviceFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    public static TheoryData<string> Members() => [nameof(GraphicsDevice.GetFormatSupport), nameof(GraphicsDevice.Dispose)];

    [Theory]
    [MemberData(nameof(Members))]
    public void OtherThread_Throws(string member)
    {
        GraphicsDevice device = _fixture.Create();

        Exception? thrown = OnOtherThread(() => GraphicsDeviceDisposeTests.Call(device, member));

        WrongThreadException wrongThread = Assert.IsType<WrongThreadException>(thrown);
        Assert.Equal("GraphicsDevice." + member, wrongThread.ApiName);
        Assert.Equal(Environment.CurrentManagedThreadId, wrongThread.ExpectedThreadId);
        // Nothing happened: the device still works on its owner thread, and its backend device is alive.
        Assert.False(_fixture.BackendDevice.IsDisposed);
        _ = device.GetFormatSupport(TextureFormat.Rgba8Unorm);
    }

    [Theory]
    [MemberData(nameof(Members))]
    public void OtherThread_AfterDispose_ThrowsWrongThreadFirst(string member)
    {
        GraphicsDevice device = _fixture.Create();
        device.Dispose();

        Exception? thrown = OnOtherThread(() => GraphicsDeviceDisposeTests.Call(device, member));

        Assert.IsType<WrongThreadException>(thrown);
    }

    [Fact]
    public void OtherThread_AdapterAndCapabilities_Work()
    {
        GraphicsDevice device = _fixture.Create();
        GraphicsAdapterInfo? adapter = null;
        GraphicsCapabilities? capabilities = null;

        Exception? thrown = OnOtherThread(() =>
        {
            adapter = device.Adapter;
            capabilities = device.Capabilities;
        });

        Assert.Null(thrown);
        Assert.Same(device.Adapter, adapter);
        Assert.Same(device.Capabilities, capabilities);
    }

    /// <summary>Runs an action on a new thread and returns what it threw, or <see langword="null"/>.</summary>
    internal static Exception? OnOtherThread(Action action)
    {
        Exception? thrown = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                thrown = exception;
            }
        });
        thread.Start();
        Assert.True(thread.Join(_joinTimeout), "The worker thread didn't finish.");
        return thrown;
    }
}
