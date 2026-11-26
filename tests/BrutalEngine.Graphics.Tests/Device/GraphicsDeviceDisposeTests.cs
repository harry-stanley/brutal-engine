using System;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Device;

/// <summary>Disposing a device, and what its members do afterwards (docs/design/graphics.md section 1).</summary>
public sealed partial class GraphicsDeviceDisposeTests : IDisposable
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

    /// <summary>Every member that checks disposal, by the name <see cref="Call"/> takes.</summary>
    public static TheoryData<string> Members() =>
    [
        "GetFormatSupport", "CreateBuffer", "CreateTexture", "GetDefaultView", "CreateTextureView", "CreateSampler",
        "Destroy(Buffer)", "Destroy(Texture)", "Destroy(TextureView)", "Destroy(Sampler)", "Destroy(ShaderModule)",
        "Destroy(BindGroupLayout)", "Destroy(PipelineLayout)", "Destroy(BindGroup)", "Destroy(GraphicsPipeline)",
        "Destroy(ComputePipeline)", "Destroy(Swapchain)", "CreateDebugLabel", "GetDebugLabelName", "Statistics", "ThrowIfLeaks",
    ];

    [Theory]
    [MemberData(nameof(Members))]
    public void AfterDispose_Throws(string member)
    {
        GraphicsDevice device = _fixture.Create();
        var prepared = Prepared.On(device);
        device.Dispose();

        ObjectDisposedException thrown = Assert.Throws<ObjectDisposedException>(() => Call(device, member, prepared));

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

    /// <summary>Calls one member by name with valid arguments: live objects of each kind made beforehand.</summary>
    internal static void Call(GraphicsDevice device, string member, Prepared prepared)
    {
        var anyHandle = new Handle(0, 1);
        switch (member)
        {
            case "GetFormatSupport":
                _ = device.GetFormatSupport(TextureFormat.Rgba8Unorm);
                break;
            case "Dispose":
                device.Dispose();
                break;
            case "CreateBuffer":
                _ = device.CreateBuffer(new BufferDescription { Size = 64, Usage = BufferUsage.Vertex });
                break;
            case "CreateTexture":
                _ = device.CreateTexture(Prepared.Texture);
                break;
            case "GetDefaultView":
                _ = device.GetDefaultView(prepared.TextureHandle);
                break;
            case "CreateTextureView":
                _ = device.CreateTextureView(new TextureViewDescription { Texture = prepared.TextureHandle });
                break;
            case "CreateSampler":
                _ = device.CreateSampler(new SamplerDescription());
                break;
            case "Destroy(Buffer)":
                device.Destroy(prepared.Buffer);
                break;
            case "Destroy(Texture)":
                device.Destroy(prepared.TextureHandle);
                break;
            case "Destroy(TextureView)":
                device.Destroy(prepared.View);
                break;
            case "Destroy(Sampler)":
                device.Destroy(prepared.Sampler);
                break;
            case "Destroy(ShaderModule)":
                device.Destroy(new ShaderModuleHandle(anyHandle));
                break;
            case "Destroy(BindGroupLayout)":
                device.Destroy(new BindGroupLayoutHandle(anyHandle));
                break;
            case "Destroy(PipelineLayout)":
                device.Destroy(new PipelineLayoutHandle(anyHandle));
                break;
            case "Destroy(BindGroup)":
                device.Destroy(new BindGroupHandle(anyHandle));
                break;
            case "Destroy(GraphicsPipeline)":
                device.Destroy(new GraphicsPipelineHandle(anyHandle));
                break;
            case "Destroy(ComputePipeline)":
                device.Destroy(new ComputePipelineHandle(anyHandle));
                break;
            case "Destroy(Swapchain)":
                device.Destroy(new SwapchainHandle(anyHandle));
                break;
            case "CreateDebugLabel":
                _ = device.CreateDebugLabel("Another pass");
                break;
            case "GetDebugLabelName":
                _ = device.GetDebugLabelName(prepared.Label);
                break;
            case "Statistics":
                _ = device.Statistics;
                break;
            case "ThrowIfLeaks":
                device.ThrowIfLeaks();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(member), member, null);
        }
    }

    /// <summary>One live object of each creatable kind, and a label, made on the owner thread for <see cref="Call"/>.</summary>
    internal sealed record Prepared(BufferHandle Buffer, TextureHandle TextureHandle, TextureViewHandle View, SamplerHandle Sampler, DebugLabel Label)
    {
        public static TextureDescription Texture => new() { Format = TextureFormat.Rgba8Unorm, Width = 4, Height = 4, Usage = TextureUsage.Sampled };

        public static Prepared On(GraphicsDevice device)
        {
            TextureHandle texture = device.CreateTexture(Texture).Value;
            return new Prepared(
                device.CreateBuffer(new BufferDescription { Size = 64, Usage = BufferUsage.Vertex }).Value,
                texture,
                device.CreateTextureView(new TextureViewDescription { Texture = texture }).Value,
                device.CreateSampler(new SamplerDescription()).Value,
                device.CreateDebugLabel("Prepared pass"));
        }

        /// <summary>Destroys every object, so the device ends with no leaks.</summary>
        public void Destroy(GraphicsDevice device)
        {
            device.Destroy(Buffer);
            device.Destroy(View);
            device.Destroy(TextureHandle);
            device.Destroy(Sampler);
        }
    }
}
