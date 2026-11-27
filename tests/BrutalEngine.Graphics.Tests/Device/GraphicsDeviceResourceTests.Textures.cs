using System;
using System.Collections.Generic;
using System.Linq;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Validation;
using BrutalEngine.TestUtilities;
using CsCheck;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Device;

/// <summary>Textures, their default views and explicit views (docs/design/graphics.md sections 1 and 5).</summary>
public sealed partial class GraphicsDeviceResourceTests
{
    [Fact]
    public void CreateTexture_DefaultView_CoversTheWholeTexture()
    {
        GraphicsDevice device = _fixture.Create();
        TextureHandle single = device.CreateTexture(Texture()).Value;
        TextureHandle array = device.CreateTexture(Texture() with { ArrayLayers = 3, MipLevels = 2 }).Value;

        TextureViewHandle singleView = device.GetDefaultView(single);
        TextureViewHandle arrayView = device.GetDefaultView(array);

        Assert.False(singleView.IsNull);
        Assert.NotEqual(singleView, arrayView);
        Assert.Equal(singleView, device.GetDefaultView(single));
        FakeBackendDevice backend = _fixture.BackendDevice;
        Assert.Equal(backend.Calls.Where(call => call.Operation == FakeBackendOperation.CreateTextureView).Select(call => call.Id),
            [backend.Objects.GetId(singleView), backend.Objects.GetId(arrayView)]);
        // The range the default view covers, through the validator a view of the texture must pass: a view one layer or
        // mip beyond it is rejected, the whole texture accepted.
        var whole = new TextureViewDescription { Texture = array, Dimension = TextureViewDimension.Texture2DArray, MipLevelCount = 2, ArrayLayerCount = 3 };
        Assert.True(device.CreateTextureView(whole).IsSuccess);
        Assert.Throws<ArgumentOutOfRangeException>(() => device.CreateTextureView(whole with { ArrayLayerCount = 4 }));
    }

    [Fact]
    public void CreateTexture_DefaultViewDimension_FollowsTheLayers()
    {
        var backend = new CountingBackend(_fixture.Backend);
        GraphicsDevice device = GraphicsDevice.Create(backend, _fixture.Options()).Value;

        TextureHandle single = device.CreateTexture(Texture() with { MipLevels = 3, DebugName = "One" }).Value;
        TextureHandle array = device.CreateTexture(Texture() with { ArrayLayers = 4, DebugName = "Four" }).Value;

        Assert.Equal(
            [
                new TextureViewDescription { Texture = single, Dimension = TextureViewDimension.Texture2D, MipLevelCount = 3, ArrayLayerCount = 1, DebugName = "One" },
                new TextureViewDescription { Texture = array, Dimension = TextureViewDimension.Texture2DArray, MipLevelCount = 1, ArrayLayerCount = 4, DebugName = "Four" },
            ],
            backend.LastDevice?.Views);
        device.Dispose();
    }

    [Fact]
    public void DestroyTexture_DestroysViews()
    {
        GraphicsDevice device = _fixture.Create();
        FakeBackendDevice backend = _fixture.BackendDevice;
        TextureHandle other = device.CreateTexture(Texture() with { DebugName = "Other" }).Value;
        TextureHandle texture = device.CreateTexture(Texture() with { MipLevels = 2 }).Value;
        TextureViewHandle defaultView = device.GetDefaultView(texture);
        TextureViewHandle mip0 = device.CreateTextureView(new TextureViewDescription { Texture = texture }).Value;
        TextureViewHandle otherView = device.CreateTextureView(new TextureViewDescription { Texture = other }).Value;
        TextureViewHandle mip1 = device.CreateTextureView(new TextureViewDescription { Texture = texture, BaseMipLevel = 1 }).Value;
        ulong[] ids = [backend.Objects.GetId(defaultView), backend.Objects.GetId(mip0), backend.Objects.GetId(mip1), backend.Objects.GetId(texture)];
        int before = backend.Calls.Count;

        device.Destroy(texture);

        // Every view of the texture, then the texture; the other texture and its view are untouched.
        Assert.Equal(ids, backend.Calls.Skip(before).Select(call => call.Id));
        Assert.All(backend.Calls.Skip(before), call => Assert.Equal(FakeBackendOperation.Destroy, call.Operation));
        foreach (TextureViewHandle view in new[] { defaultView, mip0, mip1 })
        {
            Assert.Throws<StaleHandleException>(() => device.Destroy(view));
            Assert.Throws<StaleHandleException>(() => backend.Objects.GetId(view));
        }

        Assert.Throws<StaleHandleException>(() => device.GetDefaultView(texture));
        Assert.Equal(3, backend.LiveObjectCount);
        device.Destroy(otherView);
        device.Destroy(other);
        Assert.Equal(0, backend.LiveObjectCount);
    }

    [Fact]
    public void DestroyView_DefaultView_ThrowsAndKeepsIt()
    {
        GraphicsDevice device = _fixture.Create();
        TextureHandle texture = device.CreateTexture(Texture() with { DebugName = "Atlas" }).Value;
        TextureViewHandle defaultView = device.GetDefaultView(texture);

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => device.Destroy(defaultView));

        Assert.Equal($"Destroy: {defaultView} 'Atlas' is its texture's default view, which is destroyed with the texture.", thrown.Message);
        Assert.Equal(2, _fixture.BackendDevice.LiveObjectCount);
        Assert.Equal(defaultView, device.GetDefaultView(texture));
    }

    [Fact]
    public void GetDefaultView_InvalidTexture_Throws()
    {
        GraphicsDevice device = _fixture.Create();
        TextureHandle texture = device.CreateTexture(Texture()).Value;

        Assert.Equal("texture", Assert.Throws<ArgumentException>(() => device.GetDefaultView(default)).ParamName);
        Assert.Throws<StaleHandleException>(() => device.GetDefaultView(new TextureHandle(new Handle(0, 2))));
        Assert.Throws<StaleHandleException>(() => device.GetDefaultView(new TextureHandle(new Handle(1, 1))));
        Assert.False(device.GetDefaultView(texture).IsNull);
    }

    [Fact]
    public void CreateTextureView_InvalidTexture_ThrowsBeforeTheBackend()
    {
        GraphicsDevice device = _fixture.Create();
        TextureHandle destroyed = device.CreateTexture(Texture()).Value;
        device.Destroy(destroyed);
        int calls = _fixture.BackendDevice.Calls.Count;

        Assert.Equal("description", Assert.Throws<ArgumentException>(() => device.CreateTextureView(new TextureViewDescription { Texture = default })).ParamName);
        Assert.Throws<StaleHandleException>(() => device.CreateTextureView(new TextureViewDescription { Texture = destroyed }));
        Assert.Equal(calls, _fixture.BackendDevice.Calls.Count);
    }

    public static TheoryData<string> UnsupportedRules() =>
    [
        "Sampled", "Storage", "ColorTarget", "DepthStencilTarget", "CopySource", "CopyDestination", "Multisample",
        "NoSupportAtAll", "SeveralMissing",
    ];

    [Theory]
    [MemberData(nameof(UnsupportedRules))]
    public void Unsupported_ReturnsResult(string rule)
    {
        // Rg16Float lacks exactly the flag the row names; every other format supports everything.
        const TextureFormat Format = TextureFormat.Rg16Float;
        const FormatSupport All = (FormatSupport)511;
        FormatSupport lacking = rule switch
        {
            "NoSupportAtAll" => All,
            "SeveralMissing" => FormatSupport.Storage | FormatSupport.CopySource,
            _ => Enum.Parse<FormatSupport>(rule),
        };
        _fixture.Script.FormatSupport = format => format == Format ? All & ~lacking : All;
        GraphicsDevice device = _fixture.Create();
        TextureDescription description = rule switch
        {
            "Multisample" => Texture() with { Format = Format, SampleCount = 4, Usage = TextureUsage.ColorTarget, DebugName = "Msaa" },
            "NoSupportAtAll" => Texture() with { Format = Format, Usage = TextureUsage.Sampled, DebugName = "None" },
            "SeveralMissing" => Texture() with { Format = Format, Usage = TextureUsage.Storage | TextureUsage.CopySource | TextureUsage.Sampled, DebugName = "Several" },
            _ => Texture() with { Format = Format, Usage = Enum.Parse<TextureUsage>(rule), DebugName = rule },
        };

        EngineError error = device.CreateTexture(description).Error;

        Assert.Equal(ErrorKind.Unsupported, error.Kind);
        Assert.Contains($"CreateTexture '{description.DebugName}': format Rg16Float lacks", error.Message, StringComparison.Ordinal);
        Assert.Contains((rule == "NoSupportAtAll" ? FormatSupport.Sampled : lacking).ToString(), error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_fixture.BackendDevice.Calls, call => call.Operation == FakeBackendOperation.CreateTexture);
        // The same description with the flag supported, or in another format, is created.
        Assert.True(device.CreateTexture(description with { Format = TextureFormat.Rgba16Float }).IsSuccess);
    }

    [Fact]
    public void Unsupported_SupportedSampleCount_Succeeds()
    {
        _fixture.Script.FormatSupport = _ => FormatSupport.ColorTarget | FormatSupport.Multisample;
        GraphicsDevice device = _fixture.Create();

        Assert.True(device.CreateTexture(Texture() with { SampleCount = 4, Usage = TextureUsage.ColorTarget }).IsSuccess);
        Assert.True(device.CreateTexture(Texture() with { SampleCount = 1, Usage = TextureUsage.ColorTarget }).IsSuccess);
    }

    // The fake reports ColorSampleCounts X1 | X4, so 2, 8 and 16 aren't listed; every format has Multisample.
    public static TheoryData<TextureFormat, int, bool> ColorSampleCountRows() => new()
    {
        { TextureFormat.Rgba8Unorm, 2, false },
        { TextureFormat.Rgba8Unorm, 8, false },
        { TextureFormat.Rgba16Float, 16, false },
        { TextureFormat.Rgba8Unorm, 4, true },
        { TextureFormat.Rgba16Float, 4, true },
        // Depth sample counts aren't reported yet, so a depth format needs only the Multisample flag.
        { TextureFormat.Depth32Float, 2, true },
        { TextureFormat.Depth16Unorm, 8, true },
    };

    [Theory]
    [MemberData(nameof(ColorSampleCountRows))]
    public void CreateTexture_SampleCount_MustBeAColorSampleCount(TextureFormat format, int sampleCount, bool supported)
    {
        _fixture.Script.FormatSupport = _ => FormatSupport.ColorTarget | FormatSupport.DepthStencilTarget | FormatSupport.Multisample;
        GraphicsDevice device = _fixture.Create();
        Assert.Equal(SampleCounts.X1 | SampleCounts.X4, device.Capabilities.Limits.ColorSampleCounts);
        TextureUsage usage = format is TextureFormat.Depth16Unorm or TextureFormat.Depth32Float ? TextureUsage.DepthStencilTarget : TextureUsage.ColorTarget;

        Result<TextureHandle> created = device.CreateTexture(Texture() with { Format = format, SampleCount = sampleCount, Usage = usage, DebugName = "Msaa" });

        Assert.Equal(supported, created.IsSuccess);
        if (!supported)
        {
            Assert.Equal(ErrorKind.Unsupported, created.Error.Kind);
            Assert.Equal(
                $"CreateTexture 'Msaa': {sampleCount} samples aren't supported for color format {format} on this device (supported counts: X1, X4).",
                created.Error.Message);
            Assert.DoesNotContain(_fixture.BackendDevice.Calls, call => call.Operation == FakeBackendOperation.CreateTexture);
        }
    }

    public static TheoryData<string> TextureFailureRules() =>
        ["ZeroWidth", "AboveLimit", "ViewOutsideTexture", "TextureBackendFails", "DefaultViewBackendFails", "ViewBackendFails"];

    [Theory]
    [MemberData(nameof(TextureFailureRules))]
    public void TextureValidator_And_BackendFailures_Propagate(string rule)
    {
        GraphicsDevice device = _fixture.Create();
        FakeBackendDevice backend = _fixture.BackendDevice;
        var error = new EngineError(ErrorKind.OutOfMemory, "out of device memory", -2);
        TextureHandle existing = device.CreateTexture(Texture()).Value;
        int live = backend.LiveObjectCount;
        switch (rule)
        {
            case "ZeroWidth":
            case "AboveLimit":
                TextureDescription bad = rule == "ZeroWidth" ? Texture() with { Width = 0 } : Texture() with { Height = 8193 };
                Exception expected = Assert.ThrowsAny<Exception>(() => ResourceValidator.Validate(bad, device.Capabilities.Limits, device.Capabilities.Features));
                Exception thrown = Assert.ThrowsAny<Exception>(() => device.CreateTexture(bad));
                Assert.Equal((expected.GetType(), expected.Message), (thrown.GetType(), thrown.Message));
                break;
            case "ViewOutsideTexture":
                var outside = new TextureViewDescription { Texture = existing, BaseMipLevel = 1 };
                Exception expectedView = Assert.ThrowsAny<Exception>(() => ResourceValidator.Validate(outside, Texture()));
                Exception thrownView = Assert.ThrowsAny<Exception>(() => device.CreateTextureView(outside));
                Assert.Equal((expectedView.GetType(), expectedView.Message), (thrownView.GetType(), thrownView.Message));
                break;
            case "TextureBackendFails":
                _fixture.Script.FailNext(FakeBackendOperation.CreateTexture, error);
                Assert.Equal(error, device.CreateTexture(Texture()).Error);
                break;
            case "DefaultViewBackendFails":
                // The texture was created, then its default view failed: the texture is destroyed before the failure returns.
                _fixture.Script.FailNext(FakeBackendOperation.CreateTextureView, error);
                Assert.Equal(error, device.CreateTexture(Texture()).Error);
                FakeBackendCall created = backend.Calls.Last(call => call.Operation == FakeBackendOperation.CreateTexture);
                FakeBackendCall destroyed = backend.Calls[^1];
                Assert.Equal((FakeBackendOperation.Destroy, (BackendObjectKind?)BackendObjectKind.Texture, created.Id), (destroyed.Operation, destroyed.Kind, destroyed.Id));
                break;
            case "ViewBackendFails":
                _fixture.Script.FailNext(FakeBackendOperation.CreateTextureView, error);
                Assert.Equal(error, device.CreateTextureView(new TextureViewDescription { Texture = existing }).Error);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(rule), rule, null);
        }

        Assert.Equal(live, backend.LiveObjectCount);
        // Nothing was issued: the next texture and view take the next free slots.
        TextureHandle next = device.CreateTexture(Texture()).Value;
        Assert.Equal((1u, 1u), (next.Value.Index, device.GetDefaultView(next).Value.Index));
    }

    [Fact]
    public void CreateTexture_DefaultViewCannotBeRecorded_DestroysBoth()
    {
        GraphicsDevice device = _fixture.Create();
        FakeBackendDevice backend = _fixture.BackendDevice;
        backend.Objects.BeforeGrowth = kind =>
        {
            if (kind == BackendObjectKind.TextureView)
            {
                throw new InsufficientMemoryException("Simulated: the view table can't grow.");
            }
        };

        Assert.Throws<InsufficientMemoryException>(() => device.CreateTexture(Texture()));

        Assert.Equal(0, backend.LiveObjectCount);
        Assert.Equal(
            [BackendObjectKind.TextureView, BackendObjectKind.Texture],
            backend.Calls.Where(call => call.Operation == FakeBackendOperation.Destroy).Select(call => call.Kind));
        backend.Objects.BeforeGrowth = null;
        Assert.Equal(0u, device.CreateTexture(Texture()).Value.Value.Index);
    }

    [Fact]
    public void Destroy_Textures_DoesNotAllocate()
    {
        GraphicsDevice device = _fixture.Create();
        _fixture.Script.RecordCalls = false;
        var textures = new TextureHandle[6];
        var views = new TextureViewHandle[6];
        for (int i = 0; i < textures.Length; i++)
        {
            textures[i] = device.CreateTexture(Texture()).Value;
            views[i] = device.CreateTextureView(new TextureViewDescription { Texture = textures[i] }).Value;
            _ = device.CreateTextureView(new TextureViewDescription { Texture = textures[i] }).Value;
        }

        device.Destroy(views[0]);
        device.Destroy(textures[0]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1; i < textures.Length; i++)
        {
            _ = device.GetDefaultView(textures[i]);
            device.Destroy(views[i]);
            device.Destroy(textures[i]);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(0, _fixture.BackendDevice.LiveObjectCount);
    }

    public static TheoryData<string> TextureMembers() =>
        ["CreateTexture", "GetDefaultView", "CreateTextureView", "Destroy(Texture)", "Destroy(TextureView)"];

    [Theory]
    [MemberData(nameof(TextureMembers))]
    public void TextureMembers_OtherThread_Throw(string member)
    {
        GraphicsDevice device = _fixture.Create();
        TextureHandle texture = device.CreateTexture(Texture()).Value;
        TextureViewHandle view = device.CreateTextureView(new TextureViewDescription { Texture = texture }).Value;
        int calls = _fixture.BackendDevice.Calls.Count;

        Exception? thrown = GraphicsDeviceThreadTests.OnOtherThread(() => CallTextureMember(device, member, texture, view));

        string api = member.StartsWith("Destroy", StringComparison.Ordinal) ? "GraphicsDevice.Destroy" : "GraphicsDevice." + member;
        Assert.Equal(api, Assert.IsType<WrongThreadException>(thrown).ApiName);
        Assert.Equal(calls, _fixture.BackendDevice.Calls.Count);
        device.Destroy(view);
        device.Destroy(texture);
    }

    [Theory]
    [MemberData(nameof(TextureMembers))]
    public void TextureMembers_AfterDispose_Throw(string member)
    {
        GraphicsDevice device = _fixture.Create();
        TextureHandle texture = device.CreateTexture(Texture()).Value;
        TextureViewHandle view = device.CreateTextureView(new TextureViewDescription { Texture = texture }).Value;
        device.Dispose();

        ObjectDisposedException thrown = Assert.Throws<ObjectDisposedException>(() => CallTextureMember(device, member, texture, view));

        Assert.Equal(typeof(GraphicsDevice).FullName, thrown.ObjectName);
    }

    [Fact]
    public void HandlesMatchReferenceModel()
    {
        // An operation: 0-1 create a buffer or sampler, 2 a texture, 3 a view of a live texture, 4 destroy a live object,
        // 5 destroy a dead or never-issued handle; with a pick that chooses among the candidates.
        Gen<(int Op, int Pick)> operation = Gen.Select(Gen.Frequency((2, Gen.Int[0, 3]), (2, Gen.Const(4)), (1, Gen.Const(5))), Gen.Int[0, 1000]);
        operation.Array[1, 80].Sample(
            operations =>
            {
                using var fixture = new DeviceFixture();
                GraphicsDevice device = fixture.Create();
                FakeBackendDevice backend = fixture.BackendDevice;
                var live = new List<Model>();
                var dead = new List<Model>();
                foreach ((int op, int pick) in operations)
                {
                    Model[] textures = [.. live.Where(model => model.Kind == BackendObjectKind.Texture)];
                    switch (op)
                    {
                        case 0:
                            live.Add(Model.Of(device.CreateBuffer(Buffer()).Value));
                            break;
                        case 1:
                            live.Add(Model.Of(device.CreateSampler(new SamplerDescription()).Value));
                            break;
                        case 2:
                            TextureHandle texture = device.CreateTexture(Texture()).Value;
                            live.Add(Model.Of(texture));
                            live.Add(Model.Of(device.GetDefaultView(texture), texture.Value, isDefault: true));
                            break;
                        case 3 when textures.Length > 0:
                            Handle parent = textures[pick % textures.Length].Handle;
                            live.Add(Model.Of(device.CreateTextureView(new TextureViewDescription { Texture = new TextureHandle(parent) }).Value, parent, isDefault: false));
                            break;
                        case 4 when live.Count > 0:
                            Model chosen = live[pick % live.Count];
                            if (chosen.IsDefaultView)
                            {
                                Assert.Throws<InvalidOperationException>(() => chosen.Destroy(device));
                                break;
                            }

                            chosen.Destroy(device);
                            // A texture takes every view of it along.
                            Model[] gone = [.. live.Where(model => ReferenceEquals(model, chosen) || (chosen.Kind == BackendObjectKind.Texture && model.Parent == chosen.Handle))];
                            live.RemoveAll(gone.Contains);
                            dead.AddRange(gone);
                            break;
                        case 5 when dead.Count > 0:
                            Model stale = dead[pick % dead.Count];
                            Assert.Throws<StaleHandleException>(() => stale.Destroy(device));
                            break;
                        default:
                            break;
                    }

                    Assert.Equal(live.Count, backend.LiveObjectCount);
                    Assert.Equal(live.Count, live.Select(model => model.Id(backend.Objects)).Distinct().Count());
                    foreach (Model model in dead)
                    {
                        Assert.Throws<StaleHandleException>(() => model.Id(backend.Objects));
                    }

                    foreach (Model view in live.Where(model => model.IsDefaultView))
                    {
                        Assert.Equal(view.Handle, device.GetDefaultView(new TextureHandle(view.Parent)).Value);
                    }
                }
            },
            threads: 1);
    }

    private static TextureDescription Texture() =>
        new() { Format = TextureFormat.Rgba8Unorm, Width = 16, Height = 16, Usage = TextureUsage.Sampled | TextureUsage.CopyDestination };

    private static void CallTextureMember(GraphicsDevice device, string member, TextureHandle texture, TextureViewHandle view)
    {
        switch (member)
        {
            case "CreateTexture":
                _ = device.CreateTexture(Texture());
                break;
            case "GetDefaultView":
                _ = device.GetDefaultView(texture);
                break;
            case "CreateTextureView":
                _ = device.CreateTextureView(new TextureViewDescription { Texture = texture });
                break;
            case "Destroy(Texture)":
                device.Destroy(texture);
                break;
            case "Destroy(TextureView)":
                device.Destroy(view);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(member), member, null);
        }
    }

    private static Made MakeTextureKind(GraphicsDevice device, string kind, string? debugName)
    {
        switch (kind)
        {
            case "Texture":
            case "TextureArray":
                TextureDescription description = Texture() with { ArrayLayers = kind == "Texture" ? 1 : 2, DebugName = debugName };
                TextureHandle texture = device.CreateTexture(description).Value;
                return new Made(BackendObjectKind.Texture, FakeBackendOperation.CreateTexture, texture.Value, () => device.Destroy(texture), objects => objects.GetId(texture))
                {
                    ObjectCount = 2,
                };
            case "TextureView":
                TextureHandle parent = device.CreateTexture(Texture() with { MipLevels = 2 }).Value;
                TextureViewHandle view = device.CreateTextureView(new TextureViewDescription { Texture = parent, BaseMipLevel = 1, DebugName = debugName }).Value;
                return new Made(BackendObjectKind.TextureView, FakeBackendOperation.CreateTextureView, view.Value, () => device.Destroy(view), objects => objects.GetId(view))
                {
                    // The parent's GetFormatSupport, CreateTexture and default CreateTextureView calls, and its two objects.
                    CallsBefore = 3,
                    LiveBefore = 2,
                };
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    /// <summary>The reference model's record of one object: its kind, handle, its texture for a view, and how to destroy it.</summary>
    private sealed record Model(BackendObjectKind Kind, Handle Handle, Handle Parent, bool IsDefaultView, Action<GraphicsDevice> Destroy, Func<BackendObjectTable, ulong> Id)
    {
        public static Model Of(BufferHandle buffer) =>
            new(BackendObjectKind.Buffer, buffer.Value, default, false, device => device.Destroy(buffer), objects => objects.GetId(buffer));

        public static Model Of(SamplerHandle sampler) =>
            new(BackendObjectKind.Sampler, sampler.Value, default, false, device => device.Destroy(sampler), objects => objects.GetId(sampler));

        public static Model Of(TextureHandle texture) =>
            new(BackendObjectKind.Texture, texture.Value, default, false, device => device.Destroy(texture), objects => objects.GetId(texture));

        public static Model Of(TextureViewHandle view, Handle texture, bool isDefault) =>
            new(BackendObjectKind.TextureView, view.Value, texture, isDefault, device => device.Destroy(view), objects => objects.GetId(view));
    }
}
