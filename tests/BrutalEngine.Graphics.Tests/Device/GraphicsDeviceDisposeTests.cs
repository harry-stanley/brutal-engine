using System;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Device;

/// <summary>Disposing a device, and what its members do afterwards (docs/design/graphics.md section 1).</summary>
public sealed class GraphicsDeviceDisposeTests : IDisposable
{
    private readonly DeviceFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Dispose_DisposesBackendDeviceOnce()
    {
        var backend = new CountingBackend(_fixture.Backend);
        GraphicsDevice device = GraphicsDevice.Create(backend, _fixture.Options()).Value;
        CountingBackendDevice backendDevice = Assert.IsType<CountingBackendDevice>(backend.LastDevice);
        Assert.Equal(0, backendDevice.DisposeCalls);

        device.Dispose();
        device.Dispose();
        device.Dispose();

        Assert.Equal(1, backendDevice.DisposeCalls);
        Assert.True(_fixture.BackendDevice.IsDisposed);
        Assert.Equal(0, backend.DisposeCalls);
    }

    public static TheoryData<string> Members() => [nameof(GraphicsDevice.GetFormatSupport)];

    [Theory]
    [MemberData(nameof(Members))]
    public void AfterDispose_Throws(string member)
    {
        GraphicsDevice device = _fixture.Create();
        device.Dispose();

        ObjectDisposedException thrown = Assert.Throws<ObjectDisposedException>(() => Call(device, member));

        Assert.Equal(typeof(GraphicsDevice).FullName, thrown.ObjectName);
    }

    [Fact]
    public void AfterDispose_AdapterAndCapabilities_StillWork()
    {
        GraphicsDevice device = _fixture.Create();
        device.Dispose();

        Assert.Same(_fixture.Script.Adapters[0], device.Adapter);
        Assert.Same(_fixture.BackendDevice.Capabilities, device.Capabilities);
    }

    /// <summary>Calls one member by name with valid arguments.</summary>
    internal static void Call(GraphicsDevice device, string member)
    {
        switch (member)
        {
            case nameof(GraphicsDevice.GetFormatSupport):
                _ = device.GetFormatSupport(TextureFormat.Rgba8Unorm);
                break;
            case nameof(GraphicsDevice.Dispose):
                device.Dispose();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(member), member, null);
        }
    }
}
