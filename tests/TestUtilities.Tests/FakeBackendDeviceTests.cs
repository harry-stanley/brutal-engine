using System;
using BrutalEngine.Core;
using BrutalEngine.Graphics;
using BrutalEngine.Graphics.Backend;
using Xunit;

namespace BrutalEngine.TestUtilities.Tests;

/// <summary>The fake backend device's object members, scripted failures and contract checks.</summary>
public sealed class FakeBackendDeviceTests : IDisposable
{
    private readonly FakeBackendFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    public static TheoryData<FakeBackendOperation> ObjectOperations() =>
    [
        FakeBackendOperation.EnumerateAdapters,
        FakeBackendOperation.CreateDevice,
        FakeBackendOperation.WaitIdle,
    ];

    [Theory]
    [MemberData(nameof(ObjectOperations))]
    public void ScriptedFailure_ReturnedOnce(FakeBackendOperation operation)
    {
        FakeBackendDevice device = _fixture.CreateDevice();
        var error = new EngineError(ErrorKind.OutOfMemory, "scripted", nativeCode: -2);
        _fixture.Script.FailNext(operation, error);

        Result first = Invoke(device, operation);
        FakeBackendDevice? lastAfterFirst = _fixture.Backend.LastDevice;
        Result second = Invoke(device, operation);

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
            Assert.Equal(new FakeBackendCall(operation, null, 0, -1), device.Calls[^2]);
        }
    }

    public static TheoryData<string> Violations() => ["WaitIdle after Dispose"];

    [Theory]
    [MemberData(nameof(Violations))]
    public void ContractViolation_Throws(string violation)
    {
        FakeBackendDevice device = _fixture.CreateDevice();
        Action call = violation switch
        {
            "WaitIdle after Dispose" => AfterDispose(() => device.WaitIdle()),
            _ => throw new ArgumentOutOfRangeException(nameof(violation), violation, "Unknown row."),
        };
        int callsBefore = device.Calls.Count;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(call);

        Assert.StartsWith("contract violation: ", exception.Message, StringComparison.Ordinal);
        Assert.Equal(callsBefore, device.Calls.Count);

        Action AfterDispose(Action action)
        {
            device.Dispose();
            return action;
        }
    }

    [Fact]
    public void Dispose_DestroysEverything_AndIsIdempotent()
    {
        FakeBackendDevice device = _fixture.CreateDevice();

        device.Dispose();
        device.Dispose();

        Assert.True(device.IsDisposed);
        Assert.Equal(0, device.LiveObjectCount);
        Assert.Single(device.Calls, call => call.Operation == FakeBackendOperation.Dispose);
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

    public static TheoryData<string> LaterMembers() =>
    [
        "GetFormatSupport", "CreateBuffer", "CreateTexture", "CreateTextureView", "CreateSampler", "SetDebugName", "Destroy",
        "CreateSurface", "DestroySurface", "CreateSwapchain", "DestroySwapchain", "GetSwapchainImage", "AcquireImage",
        "Present", "WaitForFrame", "IsFrameComplete", "BeginFrame", "Execute", "TryReadTimestamps",
    ];

    [Theory]
    [MemberData(nameof(LaterMembers))]
    public void LaterMember_ThrowsNotSupported(string member)
    {
        FakeBackendDevice device = _fixture.CreateDevice();
        Action call = member switch
        {
            "GetFormatSupport" => () => device.GetFormatSupport(TextureFormat.R8Unorm),
            "CreateBuffer" => () => device.CreateBuffer(FakeBackendFixture.Buffer(16), BackendMemory.HostUpload),
            "CreateTexture" => () => device.CreateTexture(FakeBackendFixture.Texture()),
            "CreateTextureView" => () => device.CreateTextureView(new TextureViewDescription { Texture = default }),
            "CreateSampler" => () => device.CreateSampler(new SamplerDescription()),
            "SetDebugName" => () => device.SetDebugName(BackendObjectKind.Buffer, 1, "x"u8),
            "Destroy" => () => device.Destroy(BackendObjectKind.Buffer, 1),
            "CreateSurface" => () => device.CreateSurface(null!),
            "DestroySurface" => () => device.DestroySurface(1),
            "CreateSwapchain" => () => device.CreateSwapchain(default),
            "DestroySwapchain" => () => device.DestroySwapchain(1),
            "GetSwapchainImage" => () => device.GetSwapchainImage(1, 0, out _, out _),
            "AcquireImage" => () => device.AcquireImage(1, 0),
            "Present" => () => device.Present(1, 0),
            "WaitForFrame" => () => device.WaitForFrame(0),
            "IsFrameComplete" => () => device.IsFrameComplete(0),
            "BeginFrame" => () => device.BeginFrame(0),
            "Execute" => () => device.Execute(default),
            "TryReadTimestamps" => () => device.TryReadTimestamps(0, []),
            _ => throw new ArgumentOutOfRangeException(nameof(member), member, "Unknown row."),
        };

        Assert.Throws<NotSupportedException>(call);
    }

    private Result Invoke(FakeBackendDevice device, FakeBackendOperation operation) => operation switch
    {
        FakeBackendOperation.EnumerateAdapters => _fixture.Backend.EnumerateAdapters(_fixture.Request()).ToResult(),
        FakeBackendOperation.CreateDevice => _fixture.Backend.CreateDevice(_fixture.Request(), 0, _fixture.Objects).ToResult(),
        FakeBackendOperation.WaitIdle => device.WaitIdle(),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Not an object operation."),
    };
}
