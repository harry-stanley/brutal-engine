using System;
using System.Linq;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Validation;
using BrutalEngine.TestUtilities;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Device;

/// <summary>
/// Creating and destroying resources through the device, against the fake backend (docs/design/graphics.md sections 1
/// and 5). The project turns background garbage collection off, which AllocationMeasurementTests guards, so the
/// allocation count is exact.
/// </summary>
public sealed partial class GraphicsDeviceResourceTests : IDisposable
{
    private readonly DeviceFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    public static TheoryData<string, string?> EachKind() => new()
    {
        { "Buffer", "Vertices" },
        { "Buffer", null },
        { "Buffer", "" },
        { "Sampler", "Pixel art" },
        { "Sampler", null },
        { "Texture", "Atlas" },
        { "Texture", null },
        { "TextureArray", "Layers" },
        { "TextureView", "Mip 1" },
        { "TextureView", null },
    };

    [Theory]
    [MemberData(nameof(EachKind))]
    public void CreateDestroy_EachKind(string kind, string? debugName)
    {
        GraphicsDevice device = _fixture.Create();
        FakeBackendDevice backend = _fixture.BackendDevice;

        Made made = Make(device, kind, debugName);

        // The calls since the object's own creation began: a texture also creates its default view.
        FakeBackendCall[] creates = [.. backend.Calls.Skip(made.CallsBefore).Where(call => call.Operation is FakeBackendOperation.CreateBuffer
            or FakeBackendOperation.CreateSampler or FakeBackendOperation.CreateTexture or FakeBackendOperation.CreateTextureView)];
        Assert.Equal(made.ObjectCount, creates.Length);
        Assert.Equal((made.CreateOperation, (BackendObjectKind?)made.Kind), (creates[0].Operation, creates[0].Kind));
        Assert.False(made.Handle.IsNull);
        Assert.Equal(creates[0].Id, made.TableId(backend.Objects));
        Assert.Equal(made.LiveBefore + made.ObjectCount, backend.LiveObjectCount);
        ulong[] named = [.. backend.Calls.Skip(made.CallsBefore).Where(call => call.Operation == FakeBackendOperation.SetDebugName).Select(call => call.Id)];
        Assert.Equal(string.IsNullOrEmpty(debugName) ? [] : creates.Select(call => call.Id), named);
        int beforeDestroy = backend.Calls.Count;

        made.Destroy();

        FakeBackendCall[] destroys = [.. backend.Calls.Skip(beforeDestroy).Where(call => call.Operation == FakeBackendOperation.Destroy)];
        // Views before their texture: the object itself goes last.
        Assert.Equal(creates.Select(call => (call.Kind, call.Id)).Reverse(), destroys.Select(call => (call.Kind, call.Id)));
        Assert.Equal(made.LiveBefore, backend.LiveObjectCount);
        Assert.Throws<StaleHandleException>(() => made.TableId(backend.Objects));
    }

    public static TheoryData<MemoryIntent, BackendMemory> MemoryIntents() => new()
    {
        { MemoryIntent.GpuOnly, BackendMemory.DeviceLocal },
        { MemoryIntent.Upload, BackendMemory.DeviceLocalHostVisiblePreferred },
        { MemoryIntent.Readback, BackendMemory.HostReadback },
    };

    [Theory]
    [MemberData(nameof(MemoryIntents))]
    public void MemoryIntent_MapsToBackendMemory(MemoryIntent intent, BackendMemory expected)
    {
        var backend = new CountingBackend(_fixture.Backend);
        using GraphicsDevice device = GraphicsDevice.Create(backend, _fixture.Options()).Value;

        BufferHandle buffer = device.CreateBuffer(new BufferDescription { Size = 64, Usage = BufferUsage.CopyDestination, Memory = intent }).Value;

        Assert.Equal(expected, backend.LastDevice?.LastBufferMemory);
        device.Destroy(buffer);
    }

    [Fact]
    public void MemoryIntent_Rows_CoverEveryValue() =>
        Assert.Equal(Enum.GetValues<MemoryIntent>(), MemoryIntents().Select(row => row.Data.Item1));

    // The expected bytes are written out from the UTF-8 encoding of each name, not computed by the code under test:
    // "Ünïcode 🎮" is U+00DC, n, U+00EF, c, o, d, e, space, U+1F3AE.
    public static TheoryData<string, string, byte[]> DebugNames() => new()
    {
        { "Buffer", "Vertices", [0x56, 0x65, 0x72, 0x74, 0x69, 0x63, 0x65, 0x73] },
        { "Buffer", "\u00DCn\u00EFcode \U0001F3AE", [0xC3, 0x9C, 0x6E, 0xC3, 0xAF, 0x63, 0x6F, 0x64, 0x65, 0x20, 0xF0, 0x9F, 0x8E, 0xAE] },
        { "Sampler", "Pixel art", [0x50, 0x69, 0x78, 0x65, 0x6C, 0x20, 0x61, 0x72, 0x74] },
        { "Sampler", "\u00DCn\u00EFcode \U0001F3AE", [0xC3, 0x9C, 0x6E, 0xC3, 0xAF, 0x63, 0x6F, 0x64, 0x65, 0x20, 0xF0, 0x9F, 0x8E, 0xAE] },
    };

    [Theory]
    [MemberData(nameof(DebugNames))]
    public void Create_DebugName_ReachesTheBackendAsUtf8(string kind, string debugName, byte[] expected)
    {
        var backend = new CountingBackend(_fixture.Backend);
        using GraphicsDevice device = GraphicsDevice.Create(backend, _fixture.Options()).Value;

        Made made = Make(device, kind, debugName);

        (BackendObjectKind Kind, ulong Id, byte[] Utf8Name) name = Assert.Single(backend.LastDevice!.Names);
        Assert.Equal((made.Kind, made.TableId(_fixture.BackendDevice.Objects)), (name.Kind, name.Id));
        Assert.Equal(expected, name.Utf8Name);
        made.Destroy();
    }

    [Theory]
    [InlineData("Buffer")]
    [InlineData("Sampler")]
    public void Destroy_Twice_ThrowsStale(string kind)
    {
        GraphicsDevice device = _fixture.Create();
        Made made = Make(device, kind, "Twice");
        made.Destroy();

        StaleHandleException thrown = Assert.Throws<StaleHandleException>(made.Destroy);

        Assert.Equal(made.Handle, thrown.Handle);
        Assert.Single(_fixture.BackendDevice.Calls, call => call.Operation == FakeBackendOperation.Destroy);
    }

    [Fact]
    public void Destroy_CopyOfDestroyedHandle_ThrowsStale()
    {
        GraphicsDevice device = _fixture.Create();
        BufferHandle buffer = device.CreateBuffer(Buffer()).Value;
        BufferHandle copy = buffer;

        device.Destroy(buffer);

        Assert.Throws<StaleHandleException>(() => device.Destroy(copy));
    }

    [Fact]
    public void Destroy_SlotReused_OldHandleStaysStale()
    {
        GraphicsDevice device = _fixture.Create();
        BufferHandle first = device.CreateBuffer(Buffer()).Value;
        device.Destroy(first);
        BufferHandle second = device.CreateBuffer(Buffer()).Value;

        Assert.Equal(first.Value.Index, second.Value.Index);
        Assert.Throws<StaleHandleException>(() => device.Destroy(first));
        device.Destroy(second);
    }

    public static TheoryData<string> Overloads() =>
    [
        "Buffer", "Texture", "TextureView", "Sampler", "ShaderModule", "BindGroupLayout", "PipelineLayout", "BindGroup",
        "GraphicsPipeline", "ComputePipeline", "Swapchain",
    ];

    [Theory]
    [MemberData(nameof(Overloads))]
    public void Destroy_Default_ThrowsArgument(string overload)
    {
        GraphicsDevice device = _fixture.Create();

        ArgumentException thrown = Assert.Throws<ArgumentException>(() => DestroyRaw(device, overload, Handle.Null));

        Assert.Equal(ParameterName(overload), thrown.ParamName);
        Assert.Contains($"The {overload} handle is null.", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_fixture.BackendDevice.Calls, call => call.Operation == FakeBackendOperation.Destroy);
    }

    public static TheoryData<string> LaterKinds() =>
        ["Texture", "TextureView", "ShaderModule", "BindGroupLayout", "PipelineLayout", "BindGroup", "GraphicsPipeline", "ComputePipeline", "Swapchain"];

    [Theory]
    [MemberData(nameof(LaterKinds))]
    public void Destroy_LaterKinds_RejectHandles(string overload)
    {
        GraphicsDevice device = _fixture.Create();
        // A live buffer and sampler in slot 0, so a lookup in the wrong kind's table would find something.
        _ = device.CreateBuffer(Buffer()).Value;
        _ = device.CreateSampler(new SamplerDescription()).Value;

        foreach (Handle handle in new[] { new Handle(0, 1), new Handle(3, 7), new Handle(uint.MaxValue - 1, uint.MaxValue) })
        {
            StaleHandleException thrown = Assert.Throws<StaleHandleException>(() => DestroyRaw(device, overload, handle));
            Assert.Equal(handle, thrown.Handle);
        }

        Assert.DoesNotContain(_fixture.BackendDevice.Calls, call => call.Operation == FakeBackendOperation.Destroy);
    }

    [Theory]
    [InlineData("Buffer")]
    [InlineData("Sampler")]
    public void Destroy_NeverIssuedHandle_ThrowsStale(string overload)
    {
        GraphicsDevice device = _fixture.Create();
        _ = device.CreateBuffer(Buffer()).Value;
        _ = device.CreateSampler(new SamplerDescription()).Value;

        Assert.Throws<StaleHandleException>(() => DestroyRaw(device, overload, new Handle(1, 1)));
        Assert.Throws<StaleHandleException>(() => DestroyRaw(device, overload, new Handle(0, 2)));
        Assert.Equal(2, _fixture.BackendDevice.LiveObjectCount);
    }

    public static TheoryData<string> FailureRules() =>
    [
        "BufferZeroSize", "BufferNoUsage", "BufferMemoryOutOfRange", "SamplerZeroAnisotropy", "SamplerAnisotropyWithoutFeature",
        "SamplerAnisotropyAboveLimit", "BufferBackendFails", "SamplerBackendFails",
    ];

    [Theory]
    [MemberData(nameof(FailureRules))]
    public void Validator_And_BackendFailures_Propagate(string rule)
    {
        // With the anisotropy feature for the limit row only, so the feature row really lacks it.
        _fixture.Script.Capabilities = FakeBackendScript.DefaultCapabilities with
        {
            Features = rule == "SamplerAnisotropyAboveLimit" ? GraphicsFeatures.SamplerAnisotropy : GraphicsFeatures.None,
            Limits = FakeBackendScript.DefaultCapabilities.Limits with { MaxSamplerAnisotropy = 8 },
        };
        GraphicsDevice device = _fixture.Create(_fixture.Options() with
        {
            OptionalFeatures = rule == "SamplerAnisotropyAboveLimit" ? GraphicsFeatures.SamplerAnisotropy : GraphicsFeatures.None,
        });
        var error = new EngineError(ErrorKind.OutOfMemory, "out of device memory", -2);
        BufferDescription buffer = Buffer();
        var sampler = new SamplerDescription { DebugName = "Failing" };
        switch (rule)
        {
            case "BufferZeroSize":
                buffer = buffer with { Size = 0 };
                break;
            case "BufferNoUsage":
                buffer = buffer with { Usage = BufferUsage.None };
                break;
            case "BufferMemoryOutOfRange":
                buffer = buffer with { Memory = (MemoryIntent)3 };
                break;
            case "SamplerZeroAnisotropy":
                sampler = sampler with { MaxAnisotropy = 0 };
                break;
            case "SamplerAnisotropyWithoutFeature":
                sampler = sampler with { MaxAnisotropy = 4 };
                break;
            case "SamplerAnisotropyAboveLimit":
                sampler = sampler with { MaxAnisotropy = 9 };
                break;
            case "BufferBackendFails":
                _fixture.Script.FailNext(FakeBackendOperation.CreateBuffer, error);
                break;
            case "SamplerBackendFails":
                _fixture.Script.FailNext(FakeBackendOperation.CreateSampler, error);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(rule), rule, null);
        }

        bool isBuffer = rule.StartsWith("Buffer", StringComparison.Ordinal);
        if (rule.EndsWith("BackendFails", StringComparison.Ordinal))
        {
            EngineError actual = isBuffer ? device.CreateBuffer(buffer).Error : device.CreateSampler(sampler).Error;
            Assert.Equal(error, actual);
        }
        else
        {
            // The validator's own exception, unchanged: the same type and message as calling it directly.
            Exception expected = Assert.ThrowsAny<Exception>(() =>
            {
                if (isBuffer)
                {
                    ResourceValidator.Validate(buffer);
                }
                else
                {
                    ResourceValidator.Validate(sampler, device.Capabilities.Limits, device.Capabilities.Features);
                }
            });
            Exception thrown = Assert.ThrowsAny<Exception>(() => _ = isBuffer ? device.CreateBuffer(buffer).IsSuccess : device.CreateSampler(sampler).IsSuccess);
            Assert.Equal(expected.GetType(), thrown.GetType());
            Assert.Equal(expected.Message, thrown.Message);
            Assert.DoesNotContain(_fixture.BackendDevice.Calls, call => call.Operation is FakeBackendOperation.CreateBuffer or FakeBackendOperation.CreateSampler);
        }

        // Nothing was issued: the next object of the kind gets the first slot.
        Made next = Make(device, isBuffer ? "Buffer" : "Sampler", null);
        Assert.Equal(0u, next.Handle.Index);
        Assert.Equal(1, _fixture.BackendDevice.LiveObjectCount);
    }

    [Theory]
    [InlineData("Buffer")]
    [InlineData("Sampler")]
    public void Create_HandleTableCannotGrow_DestroysTheBackendObject(string kind)
    {
        GraphicsDevice device = _fixture.Create();
        FakeBackendDevice backend = _fixture.BackendDevice;
        backend.Objects.BeforeGrowth = _ => throw new InsufficientMemoryException("Simulated: the table can't grow.");

        Assert.Throws<InsufficientMemoryException>(() => Make(device, kind, "Lost"));

        FakeBackendCall destroy = Assert.Single(backend.Calls, call => call.Operation == FakeBackendOperation.Destroy);
        Assert.Equal(Assert.Single(backend.Calls, call => call.Operation is FakeBackendOperation.CreateBuffer or FakeBackendOperation.CreateSampler).Id, destroy.Id);
        Assert.DoesNotContain(backend.Calls, call => call.Operation == FakeBackendOperation.SetDebugName);
        Assert.Equal(0, backend.LiveObjectCount);
        backend.Objects.BeforeGrowth = null;
        Assert.Equal(0u, Make(device, kind, null).Handle.Index);
    }

    [Fact]
    public void Dispose_DestroysLiveObjects_NewestFirst()
    {
        GraphicsDevice device = _fixture.Create();
        FakeBackendDevice backend = _fixture.BackendDevice;
        BufferHandle first = device.CreateBuffer(Buffer()).Value;
        _ = device.CreateSampler(new SamplerDescription()).Value;
        _ = device.CreateBuffer(Buffer()).Value;
        _ = device.CreateSampler(new SamplerDescription()).Value;
        device.Destroy(first);
        ulong[] created = [.. backend.Calls.Where(call => call.Operation is FakeBackendOperation.CreateBuffer or FakeBackendOperation.CreateSampler).Select(call => call.Id)];
        int before = backend.Calls.Count;

        device.Dispose();

        Assert.Equal(
            [
                (FakeBackendOperation.Destroy, (BackendObjectKind?)BackendObjectKind.Sampler, created[3]),
                (FakeBackendOperation.Destroy, BackendObjectKind.Buffer, created[2]),
                (FakeBackendOperation.Destroy, BackendObjectKind.Sampler, created[1]),
                (FakeBackendOperation.Dispose, null, 0UL),
            ],
            backend.Calls.Skip(before).Select(call => (call.Operation, call.Kind, call.Id)));
    }

    [Fact]
    public void Destroy_DoesNotAllocate()
    {
        GraphicsDevice device = _fixture.Create();
        _fixture.Script.RecordCalls = false;
        var buffers = new BufferHandle[8];
        var samplers = new SamplerHandle[8];
        for (int i = 0; i < buffers.Length; i++)
        {
            buffers[i] = device.CreateBuffer(Buffer()).Value;
            samplers[i] = device.CreateSampler(new SamplerDescription()).Value;
        }

        // Warm up the paths once, then measure the rest.
        device.Destroy(buffers[0]);
        device.Destroy(samplers[0]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1; i < buffers.Length; i++)
        {
            device.Destroy(buffers[i]);
            device.Destroy(samplers[i]);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(0, _fixture.BackendDevice.LiveObjectCount);
    }

    public static TheoryData<string> ResourceMembers() =>
        ["CreateBuffer", "CreateSampler", "Destroy(Buffer)", "Destroy(Sampler)", "Destroy(ShaderModule)", "Destroy(Swapchain)"];

    [Theory]
    [MemberData(nameof(ResourceMembers))]
    public void ResourceMembers_OtherThread_Throw(string member)
    {
        GraphicsDevice device = _fixture.Create();
        BufferHandle buffer = device.CreateBuffer(Buffer()).Value;
        SamplerHandle sampler = device.CreateSampler(new SamplerDescription()).Value;
        int calls = _fixture.BackendDevice.Calls.Count;

        Exception? thrown = GraphicsDeviceThreadTests.OnOtherThread(() => CallMember(device, member, buffer, sampler));

        string api = member.StartsWith("Destroy", StringComparison.Ordinal) ? "GraphicsDevice.Destroy" : "GraphicsDevice." + member;
        Assert.Equal(api, Assert.IsType<WrongThreadException>(thrown).ApiName);
        // Nothing reached the backend, and the handles are still live on the owner thread.
        Assert.Equal(calls, _fixture.BackendDevice.Calls.Count);
        device.Destroy(buffer);
        device.Destroy(sampler);
    }

    [Theory]
    [MemberData(nameof(ResourceMembers))]
    public void ResourceMembers_AfterDispose_Throw(string member)
    {
        GraphicsDevice device = _fixture.Create();
        BufferHandle buffer = device.CreateBuffer(Buffer()).Value;
        SamplerHandle sampler = device.CreateSampler(new SamplerDescription()).Value;
        device.Dispose();

        ObjectDisposedException thrown = Assert.Throws<ObjectDisposedException>(() => CallMember(device, member, buffer, sampler));

        Assert.Equal(typeof(GraphicsDevice).FullName, thrown.ObjectName);
    }

    private static BufferDescription Buffer() => new() { Size = 256, Usage = BufferUsage.Vertex | BufferUsage.CopyDestination };

    private static void CallMember(GraphicsDevice device, string member, BufferHandle buffer, SamplerHandle sampler)
    {
        switch (member)
        {
            case "CreateBuffer":
                _ = device.CreateBuffer(Buffer());
                break;
            case "CreateSampler":
                _ = device.CreateSampler(new SamplerDescription());
                break;
            case "Destroy(Buffer)":
                device.Destroy(buffer);
                break;
            case "Destroy(Sampler)":
                device.Destroy(sampler);
                break;
            case "Destroy(ShaderModule)":
                device.Destroy(new ShaderModuleHandle(new Handle(0, 1)));
                break;
            case "Destroy(Swapchain)":
                device.Destroy(new SwapchainHandle(new Handle(0, 1)));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(member), member, null);
        }
    }

    private static string ParameterName(string overload) => overload switch
    {
        "Buffer" => "buffer",
        "Texture" => "texture",
        "TextureView" => "view",
        "Sampler" => "sampler",
        "ShaderModule" => "shaderModule",
        "BindGroupLayout" or "PipelineLayout" => "layout",
        "BindGroup" => "bindGroup",
        "GraphicsPipeline" or "ComputePipeline" => "pipeline",
        "Swapchain" => "swapchain",
        _ => throw new ArgumentOutOfRangeException(nameof(overload), overload, null),
    };

    private static void DestroyRaw(GraphicsDevice device, string overload, Handle handle)
    {
        switch (overload)
        {
            case "Buffer":
                device.Destroy(new BufferHandle(handle));
                break;
            case "Texture":
                device.Destroy(new TextureHandle(handle));
                break;
            case "TextureView":
                device.Destroy(new TextureViewHandle(handle));
                break;
            case "Sampler":
                device.Destroy(new SamplerHandle(handle));
                break;
            case "ShaderModule":
                device.Destroy(new ShaderModuleHandle(handle));
                break;
            case "BindGroupLayout":
                device.Destroy(new BindGroupLayoutHandle(handle));
                break;
            case "PipelineLayout":
                device.Destroy(new PipelineLayoutHandle(handle));
                break;
            case "BindGroup":
                device.Destroy(new BindGroupHandle(handle));
                break;
            case "GraphicsPipeline":
                device.Destroy(new GraphicsPipelineHandle(handle));
                break;
            case "ComputePipeline":
                device.Destroy(new ComputePipelineHandle(handle));
                break;
            case "Swapchain":
                device.Destroy(new SwapchainHandle(handle));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(overload), overload, null);
        }
    }

    /// <summary>Creates one object of a kind and returns how to look it up and destroy it.</summary>
    private static Made Make(GraphicsDevice device, string kind, string? debugName)
    {
        switch (kind)
        {
            case "Buffer":
                BufferHandle buffer = device.CreateBuffer(Buffer() with { DebugName = debugName }).Value;
                return new Made(BackendObjectKind.Buffer, FakeBackendOperation.CreateBuffer, buffer.Value, () => device.Destroy(buffer), objects => objects.GetId(buffer));
            case "Sampler":
                SamplerHandle sampler = device.CreateSampler(new SamplerDescription { DebugName = debugName }).Value;
                return new Made(BackendObjectKind.Sampler, FakeBackendOperation.CreateSampler, sampler.Value, () => device.Destroy(sampler), objects => objects.GetId(sampler));
            default:
                return MakeTextureKind(device, kind, debugName);
        }
    }

    /// <summary>One object a test made, and how to look it up and destroy it.</summary>
    /// <remarks>
    /// <see cref="CallsBefore"/> and <see cref="LiveBefore"/> are the backend's call count and live objects when its own
    /// creation began (a view's texture comes before); <see cref="ObjectCount"/> backend objects are created with it.
    /// </remarks>
    private sealed record Made(BackendObjectKind Kind, FakeBackendOperation CreateOperation, Handle Handle, Action Destroy, Func<BackendObjectTable, ulong> TableId)
    {
        public int CallsBefore { get; init; }

        public int LiveBefore { get; init; }

        public int ObjectCount { get; init; } = 1;
    }
}
