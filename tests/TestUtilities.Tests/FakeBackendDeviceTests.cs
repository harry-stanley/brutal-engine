using System;
using System.Collections.Generic;
using BrutalEngine.Core;
using BrutalEngine.Graphics;
using BrutalEngine.Graphics.Backend;
using Xunit;

namespace BrutalEngine.TestUtilities.Tests;

/// <summary>The fake backend device's object members, scripted failures and contract checks.</summary>
public sealed partial class FakeBackendDeviceTests : IDisposable
{
    private readonly FakeBackendFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Ids_NonZeroAndUnique()
    {
        FakeBackendDevice device = _fixture.CreateDevice();
        var ids = new List<ulong>();
        for (int round = 0; round < 3; round++)
        {
            ulong buffer = device.CreateBuffer(FakeBackendFixture.Buffer(16), BackendMemory.HostUpload).Value.Id;
            ulong texture = device.CreateTexture(FakeBackendFixture.Texture()).Value;
            ulong view = device.CreateTextureView(_fixture.ViewOf(texture, (uint)round + 1)).Value;
            ulong sampler = device.CreateSampler(new SamplerDescription()).Value;
            ids.AddRange([buffer, texture, view, sampler]);

            // Destroying before the next round frees ids an allocator that reuses them would hand out again.
            device.Destroy(BackendObjectKind.TextureView, view);
            device.Destroy(BackendObjectKind.Texture, texture);
            device.Destroy(BackendObjectKind.Buffer, buffer);
            device.Destroy(BackendObjectKind.Sampler, sampler);
        }

        Assert.DoesNotContain(0UL, ids);
        Assert.Equal(ids.Count, new HashSet<ulong>(ids).Count);
        Assert.Equal(0, device.LiveObjectCount);
    }

    public static TheoryData<FakeBackendOperation> ObjectOperations() =>
    [
        FakeBackendOperation.EnumerateAdapters,
        FakeBackendOperation.CreateDevice,
        FakeBackendOperation.CreateBuffer,
        FakeBackendOperation.CreateTexture,
        FakeBackendOperation.CreateTextureView,
        FakeBackendOperation.CreateSampler,
        FakeBackendOperation.WaitIdle,
        FakeBackendOperation.CreateSurface,
        FakeBackendOperation.CreateSwapchain,
    ];

    [Theory]
    [MemberData(nameof(ObjectOperations))]
    public void ScriptedFailure_ReturnedOnce(FakeBackendOperation operation)
    {
        FakeBackendDevice device = _fixture.CreateDevice();
        ulong texture = device.CreateTexture(FakeBackendFixture.Texture()).Value;
        TextureViewDescription view = _fixture.ViewOf(texture, 1);
        WindowSurfaceSource source = FakeBackendFixture.Window();
        ulong surface = operation is FakeBackendOperation.CreateSwapchain ? device.CreateSurface(source).Value : 0;
        int liveBefore = device.LiveObjectCount;
        var error = new EngineError(ErrorKind.OutOfMemory, "scripted", nativeCode: -2);
        _fixture.Script.FailNext(operation, error);

        Result first = Invoke(device, operation, view, surface, source);
        FakeBackendDevice? lastAfterFirst = _fixture.Backend.LastDevice;
        Result second = Invoke(device, operation, view, surface, source);

        Assert.True(first.IsFailure);
        Assert.Equal(error, first.Error);
        Assert.True(second.IsSuccess, second.ToString());
        if (operation is FakeBackendOperation.CreateDevice)
        {
            // The failed creation made no device; the next one did.
            Assert.Same(device, lastAfterFirst);
            Assert.NotSame(device, _fixture.Backend.LastDevice);
            device.Dispose();
        }
        else if (operation is not FakeBackendOperation.EnumerateAdapters)
        {
            // The failed call is recorded with no id and created nothing; the second created one object, or none.
            int created = operation is FakeBackendOperation.WaitIdle ? 0 : 1;
            Assert.Equal(liveBefore + created, device.LiveObjectCount);
            Assert.Equal(new FakeBackendCall(operation, KindOf(operation), 0, -1), device.Calls[^2]);
        }
    }

    public static TheoryData<string> Violations() =>
    [
        "Destroy unknown id",
        "Destroy id 0",
        "Destroy twice",
        "Destroy wrong kind",
        "SetDebugName unknown id",
        "SetDebugName destroyed id",
        "SetDebugName wrong kind",
        "CreateTextureView of a destroyed texture",
        "CreateTextureView of a non-texture id",
        "GetFormatSupport Undefined",
        "GetFormatSupport below range",
        "GetFormatSupport above range",
        "CreateBuffer size 0",
        "CreateBuffer memory below range",
        "CreateBuffer memory above range",
        "CreateTexture after Dispose",
        "WaitIdle after Dispose",
        "Destroy after Dispose",
        .. _swapchainViolations,
    ];

    [Theory]
    [MemberData(nameof(Violations))]
    public void ContractViolation_Throws(string violation)
    {
        FakeBackendDevice device = _fixture.CreateDevice();
        ulong buffer = device.CreateBuffer(FakeBackendFixture.Buffer(16), BackendMemory.DeviceLocal).Value.Id;
        ulong texture = device.CreateTexture(FakeBackendFixture.Texture()).Value;
        ulong destroyed = device.CreateTexture(FakeBackendFixture.Texture()).Value;
        device.Destroy(BackendObjectKind.Texture, destroyed);
        Action call = violation switch
        {
            "Destroy unknown id" => () => device.Destroy(BackendObjectKind.Buffer, 999),
            "Destroy id 0" => () => device.Destroy(BackendObjectKind.Buffer, 0),
            "Destroy twice" => () => device.Destroy(BackendObjectKind.Texture, destroyed),
            "Destroy wrong kind" => () => device.Destroy(BackendObjectKind.Texture, buffer),
            "SetDebugName unknown id" => () => device.SetDebugName(BackendObjectKind.Buffer, 999, "x"u8),
            "SetDebugName destroyed id" => () => device.SetDebugName(BackendObjectKind.Texture, destroyed, "x"u8),
            "SetDebugName wrong kind" => () => device.SetDebugName(BackendObjectKind.Sampler, texture, "x"u8),
            "CreateTextureView of a destroyed texture" => View(_fixture.ViewOf(destroyed, 1)),
            "CreateTextureView of a non-texture id" => View(_fixture.ViewOf(buffer, 1)),
            "GetFormatSupport Undefined" => () => device.GetFormatSupport(TextureFormat.Undefined),
            "GetFormatSupport below range" => () => device.GetFormatSupport((TextureFormat)(-1)),
            "GetFormatSupport above range" => () => device.GetFormatSupport(TextureFormat.Depth32Float + 1),
            "CreateBuffer size 0" => () => device.CreateBuffer(FakeBackendFixture.Buffer(0), BackendMemory.HostUpload),
            "CreateBuffer memory below range" => () => device.CreateBuffer(FakeBackendFixture.Buffer(16), (BackendMemory)(-1)),
            "CreateBuffer memory above range" => () => device.CreateBuffer(FakeBackendFixture.Buffer(16), BackendMemory.HostReadback + 1),
            "CreateTexture after Dispose" => AfterDispose(() => device.CreateTexture(FakeBackendFixture.Texture())),
            "WaitIdle after Dispose" => AfterDispose(() => device.WaitIdle()),
            "Destroy after Dispose" => AfterDispose(() => device.Destroy(BackendObjectKind.Buffer, buffer)),
            _ => SwapchainViolation(device, violation),
        };
        int callsBefore = device.Calls.Count;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(call);

        Assert.StartsWith("contract violation: ", exception.Message, StringComparison.Ordinal);
        Assert.Equal(callsBefore, device.Calls.Count);

        Action View(TextureViewDescription description) => () => device.CreateTextureView(description);

        Action AfterDispose(Action action)
        {
            device.Dispose();
            return action;
        }
    }

    [Fact]
    public unsafe void MappedMemory_IsWritableAndFreedAtDestroy()
    {
        FakeBackendDevice device = _fixture.CreateDevice();
        BackendBuffer upload = device.CreateBuffer(FakeBackendFixture.Buffer(64), BackendMemory.HostUpload).Value;
        BackendBuffer readback = device.CreateBuffer(FakeBackendFixture.Buffer(24), BackendMemory.HostReadback).Value;
        BackendBuffer local = device.CreateBuffer(FakeBackendFixture.Buffer(32), BackendMemory.DeviceLocal).Value;
        BackendBuffer preferred = device.CreateBuffer(FakeBackendFixture.Buffer(32), BackendMemory.DeviceLocalHostVisiblePreferred).Value;

        Assert.Equal(((nint)0, (nint)0), (local.MappedPointer, preferred.MappedPointer));
        Assert.Equal((2, 88L), (_fixture.Allocator.LiveAllocationCount, _fixture.Allocator.LiveBytes));
        // Sound: each pointer is a live block of exactly the buffer's size, from the fake's allocator.
        var uploadBytes = new Span<byte>((void*)upload.MappedPointer, 64);
        var readbackBytes = new Span<byte>((void*)readback.MappedPointer, 24);
        Assert.True(uploadBytes.IndexOfAnyExcept((byte)0) < 0 && readbackBytes.IndexOfAnyExcept((byte)0) < 0);
        uploadBytes.Fill(0xA5);
        readbackBytes.Fill(0x5A);
        Assert.True(uploadBytes.IndexOfAnyExcept((byte)0xA5) < 0 && readbackBytes.IndexOfAnyExcept((byte)0x5A) < 0);

        device.Destroy(BackendObjectKind.Buffer, upload.Id);
        Assert.Equal((1, 24L), (_fixture.Allocator.LiveAllocationCount, _fixture.Allocator.LiveBytes));

        device.Dispose();
        Assert.Equal(0, _fixture.Allocator.LiveAllocationCount);
    }

    [Fact]
    public void Dispose_DestroysEverything_AndIsIdempotent()
    {
        FakeBackendDevice device = _fixture.CreateDevice();
        _ = device.CreateBuffer(FakeBackendFixture.Buffer(8), BackendMemory.HostReadback).Value;
        _ = device.CreateSampler(new SamplerDescription()).Value;

        device.Dispose();
        device.Dispose();

        Assert.True(device.IsDisposed);
        Assert.Equal(0, device.LiveObjectCount);
        Assert.Equal(0, _fixture.Allocator.LiveAllocationCount);
        Assert.Single(device.Calls, call => call.Operation == FakeBackendOperation.Dispose);
    }

    [Fact]
    public void Calls_RecordOperationKindIdAndSlot()
    {
        FakeBackendDevice device = _fixture.CreateDevice(validation: true);
        ulong buffer = device.CreateBuffer(FakeBackendFixture.Buffer(8), BackendMemory.DeviceLocal).Value.Id;
        ulong texture = device.CreateTexture(FakeBackendFixture.Texture()).Value;
        ulong view = device.CreateTextureView(_fixture.ViewOf(texture, 1)).Value;
        ulong sampler = device.CreateSampler(new SamplerDescription()).Value;
        device.SetDebugName(BackendObjectKind.Sampler, sampler, "linear"u8);
        _ = device.GetFormatSupport(TextureFormat.R8Unorm);
        device.Destroy(BackendObjectKind.Buffer, buffer);
        Assert.True(device.WaitIdle().IsSuccess);
        device.InjectValidationError();
        device.Dispose();

        FakeBackendCall[] expected =
            [
                new(FakeBackendOperation.CreateBuffer, BackendObjectKind.Buffer, buffer, -1),
                new(FakeBackendOperation.CreateTexture, BackendObjectKind.Texture, texture, -1),
                new(FakeBackendOperation.CreateTextureView, BackendObjectKind.TextureView, view, -1),
                new(FakeBackendOperation.CreateSampler, BackendObjectKind.Sampler, sampler, -1),
                new(FakeBackendOperation.SetDebugName, BackendObjectKind.Sampler, sampler, -1),
                new(FakeBackendOperation.GetFormatSupport, null, 0, -1),
                new(FakeBackendOperation.Destroy, BackendObjectKind.Buffer, buffer, -1),
                new(FakeBackendOperation.WaitIdle, null, 0, -1),
                new(FakeBackendOperation.InjectValidationError, null, 0, -1),
                new(FakeBackendOperation.Dispose, null, 0, -1),
            ];
        Assert.Equal(expected, device.Calls);
    }

    [Fact]
    public void GetFormatSupport_ReturnsScriptedSupport()
    {
        _fixture.Script.FormatSupport = format => format == TextureFormat.R8Unorm ? FormatSupport.Storage : FormatSupport.None;
        FakeBackendDevice device = _fixture.CreateDevice();

        Assert.Equal(FormatSupport.Storage, device.GetFormatSupport(TextureFormat.R8Unorm));
        Assert.Equal(FormatSupport.None, device.GetFormatSupport(TextureFormat.Depth32Float));
    }

    [Fact]
    public void GetFormatSupport_AcceptsEveryDefinedFormatButUndefined()
    {
        FakeBackendDevice device = _fixture.CreateDevice();

        foreach (TextureFormat format in Enum.GetValues<TextureFormat>())
        {
            if (format != TextureFormat.Undefined)
            {
                Assert.Equal(FakeBackendScript.DefaultFormatSupport(format), device.GetFormatSupport(format));
            }
        }
    }

    [Fact]
    public void InjectValidationError_WithoutValidation_Throws()
    {
        FakeBackendDevice device = _fixture.CreateDevice(validation: false);

        Assert.Throws<InvalidOperationException>(device.InjectValidationError);
        Assert.Throws<InvalidOperationException>(() => device.AddValidationMessages(1, 0));
        Assert.Equal((0L, 0L), (device.ValidationErrors, device.ValidationWarnings));
    }

    [Fact]
    public void AddValidationMessages_GrowsTheCounters()
    {
        FakeBackendDevice device = _fixture.CreateDevice(validation: true);

        device.AddValidationMessages(2, 3);
        device.AddValidationMessages(1, 0);

        Assert.Equal((3L, 3L), (device.ValidationErrors, device.ValidationWarnings));
        Assert.Throws<ArgumentOutOfRangeException>(() => device.AddValidationMessages(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => device.AddValidationMessages(0, -1));
    }

    public static TheoryData<string> LaterMembers() => ["WaitForFrame", "IsFrameComplete", "BeginFrame", "Execute", "TryReadTimestamps"];

    [Theory]
    [MemberData(nameof(LaterMembers))]
    public void LaterMember_ThrowsNotSupported(string member)
    {
        FakeBackendDevice device = _fixture.CreateDevice();
        Action call = member switch
        {
            "WaitForFrame" => () => device.WaitForFrame(0),
            "IsFrameComplete" => () => device.IsFrameComplete(0),
            "BeginFrame" => () => device.BeginFrame(0),
            "Execute" => () => device.Execute(default),
            "TryReadTimestamps" => () => device.TryReadTimestamps(0, []),
            _ => throw new ArgumentOutOfRangeException(nameof(member), member, "Unknown row."),
        };

        Assert.Throws<NotSupportedException>(call);
    }

    private Result Invoke(
        FakeBackendDevice device, FakeBackendOperation operation, TextureViewDescription view, ulong surface, WindowSurfaceSource source)
    {
        return operation switch
        {
            FakeBackendOperation.EnumerateAdapters => _fixture.Backend.EnumerateAdapters(_fixture.Request()).ToResult(),
            FakeBackendOperation.CreateDevice => _fixture.Backend.CreateDevice(_fixture.Request(), 0, _fixture.Objects).ToResult(),
            FakeBackendOperation.CreateBuffer => device.CreateBuffer(FakeBackendFixture.Buffer(16), BackendMemory.HostUpload).ToResult(),
            FakeBackendOperation.CreateTexture => device.CreateTexture(FakeBackendFixture.Texture()).ToResult(),
            FakeBackendOperation.CreateTextureView => device.CreateTextureView(view).ToResult(),
            FakeBackendOperation.CreateSampler => device.CreateSampler(new SamplerDescription()).ToResult(),
            FakeBackendOperation.WaitIdle => device.WaitIdle(),
            FakeBackendOperation.CreateSurface => device.CreateSurface(FakeBackendFixture.Window()).ToResult(),
            FakeBackendOperation.CreateSwapchain => device.CreateSwapchain(FakeBackendFixture.Swapchain(surface, source)).ToResult(),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Not an object operation."),
        };
    }

    private static BackendObjectKind? KindOf(FakeBackendOperation operation) => operation switch
    {
        FakeBackendOperation.CreateBuffer => BackendObjectKind.Buffer,
        FakeBackendOperation.CreateTexture => BackendObjectKind.Texture,
        FakeBackendOperation.CreateTextureView => BackendObjectKind.TextureView,
        FakeBackendOperation.CreateSampler => BackendObjectKind.Sampler,
        _ => null,
    };
}
