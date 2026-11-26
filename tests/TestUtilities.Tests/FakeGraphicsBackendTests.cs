using System;
using System.Collections.Generic;
using BrutalEngine.Core;
using BrutalEngine.Graphics;
using BrutalEngine.Graphics.Backend;
using Xunit;

namespace BrutalEngine.TestUtilities.Tests;

/// <summary>The fake backend's adapters and device creation, and its script.</summary>
public sealed class FakeGraphicsBackendTests : IDisposable
{
    private readonly FakeBackendFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void EnumerateAdapters_ReturnsACopyOfTheScriptedAdapters()
    {
        _fixture.Script.Adapters.Add(FakeBackendScript.CreateAdapter(1, "no presentation"));

        IReadOnlyList<GraphicsAdapterInfo> adapters = _fixture.Backend.EnumerateAdapters(_fixture.Request()).Value;
        _fixture.Script.Adapters.Clear();

        Assert.Equal(2, adapters.Count);
        Assert.Equal((0, "Fake GPU 0", 0), (adapters[0].Index, adapters[0].Name, adapters[0].RejectionReasons.Count));
        Assert.Equal((1, "no presentation"), (adapters[1].Index, Assert.Single(adapters[1].RejectionReasons)));
        Assert.Equal("Fake", _fixture.Backend.Name);
    }

    [Fact]
    public void EnumerateAdapters_AdapterIndexNotItsPosition_Throws()
    {
        _fixture.Script.Adapters.Add(FakeBackendScript.CreateAdapter(2));

        Assert.Throws<InvalidOperationException>(() => _fixture.Backend.EnumerateAdapters(_fixture.Request()));
    }

    [Fact]
    public void CreateDevice_ReportsTheAdapterAndScriptedCapabilities()
    {
        _fixture.Script.Adapters.Add(FakeBackendScript.CreateAdapter(1));
        GraphicsCapabilities capabilities = FakeBackendScript.DefaultCapabilities with { Features = GraphicsFeatures.TimestampQueries };
        _fixture.Script.Capabilities = capabilities;
        BackendDeviceRequest request = _fixture.Request();

        FakeBackendDevice device = Assert.IsType<FakeBackendDevice>(_fixture.Backend.CreateDevice(request, 1, _fixture.Objects).Value);

        Assert.Same(device, _fixture.Backend.LastDevice);
        Assert.Same(request, device.Request);
        Assert.Same(_fixture.Objects, device.Objects);
        Assert.Same(_fixture.Script.Adapters[1], device.Adapter);
        Assert.Same(capabilities, device.Capabilities);
        Assert.False(device.IsDisposed);
        Assert.Empty(device.Calls);
    }

    public static TheoryData<string> Violations() =>
        ["adapter index -1", "adapter index past the end", "rejected adapter", "no frames in flight", "4 frames in flight"];

    [Theory]
    [MemberData(nameof(Violations))]
    public void ContractViolation_Throws(string violation)
    {
        _fixture.Script.Adapters.Add(FakeBackendScript.CreateAdapter(1, "too old"));
        (int adapter, int frames) = violation switch
        {
            "adapter index -1" => (-1, 2),
            "adapter index past the end" => (2, 2),
            "rejected adapter" => (1, 2),
            "no frames in flight" => (0, 0),
            "4 frames in flight" => (0, 4),
            _ => throw new ArgumentOutOfRangeException(nameof(violation), violation, "Unknown row."),
        };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => _fixture.Backend.CreateDevice(_fixture.Request(framesInFlight: frames), adapter, _fixture.Objects));

        Assert.StartsWith("contract violation: ", exception.Message, StringComparison.Ordinal);
        Assert.Null(_fixture.Backend.LastDevice);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CreateDevice_FramesInFlightFrom1To3_Succeeds(int framesInFlight)
    {
        FakeBackendDevice device = Assert.IsType<FakeBackendDevice>(
            _fixture.Backend.CreateDevice(_fixture.Request(framesInFlight: framesInFlight), 0, _fixture.Objects).Value);

        Assert.Equal(framesInFlight, device.Request.FramesInFlight);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new FakeGraphicsBackend(null!));
        Assert.Throws<ArgumentNullException>(() => _fixture.Backend.EnumerateAdapters(null!));
        Assert.Throws<ArgumentNullException>(() => _fixture.Backend.CreateDevice(null!, 0, _fixture.Objects));
        Assert.Throws<ArgumentNullException>(() => _fixture.Backend.CreateDevice(_fixture.Request(), 0, null!));
        Assert.Throws<ArgumentNullException>(() => _fixture.Script.Capabilities = null!);
        Assert.Throws<ArgumentNullException>(() => _fixture.Script.FormatSupport = null!);
        Assert.Throws<ArgumentNullException>(() => _fixture.Script.SwapchainInfo = null!);
    }

    public static TheoryData<FakeBackendOperation> NonResultOperations() =>
    [
        FakeBackendOperation.GetFormatSupport, FakeBackendOperation.SetDebugName, FakeBackendOperation.Destroy,
        FakeBackendOperation.DestroySurface, FakeBackendOperation.DestroySwapchain, FakeBackendOperation.GetSwapchainImage,
        FakeBackendOperation.AcquireImage, FakeBackendOperation.Present, FakeBackendOperation.TryReadTimestamps,
        FakeBackendOperation.InjectValidationError, FakeBackendOperation.Dispose,
    ];

    [Theory]
    [MemberData(nameof(NonResultOperations))]
    public void FailNext_OperationWithoutResult_Throws(FakeBackendOperation operation) =>
        Assert.Throws<ArgumentException>(() => _fixture.Script.FailNext(operation, new EngineError(ErrorKind.Native, "x")));

    [Fact]
    public void FailNext_UndefinedOperation_Throws()
    {
        var error = new EngineError(ErrorKind.Native, "x");

        Assert.Throws<ArgumentOutOfRangeException>(() => _fixture.Script.FailNext((FakeBackendOperation)(-1), error));
        Assert.Throws<ArgumentOutOfRangeException>(() => _fixture.Script.FailNext(FakeBackendOperation.Dispose + 1, error));
    }

    [Fact]
    public void FailNext_AlreadyPending_Throws()
    {
        var error = new EngineError(ErrorKind.Timeout, "x");
        _fixture.Script.FailNext(FakeBackendOperation.WaitIdle, error);

        Assert.Throws<InvalidOperationException>(() => _fixture.Script.FailNext(FakeBackendOperation.WaitIdle, error));
    }

    [Theory]
    [InlineData(FakeBackendOperation.CreateSwapchain)]
    [InlineData(FakeBackendOperation.WaitForFrame)]
    [InlineData(FakeBackendOperation.Execute)]
    public void SwapchainStatusNext_NotASwapchainOperation_Throws(FakeBackendOperation operation) =>
        Assert.Throws<ArgumentException>(() => _fixture.Script.SwapchainStatusNext(operation, SwapchainStatus.OutOfDate, 0));

    [Fact]
    public void SwapchainStatusNext_UndefinedArguments_Throw()
    {
        FakeBackendScript script = _fixture.Script;

        Assert.Throws<ArgumentOutOfRangeException>(() => script.SwapchainStatusNext((FakeBackendOperation)(-1), SwapchainStatus.OutOfDate, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => script.SwapchainStatusNext(FakeBackendOperation.Present, (SwapchainStatus)(-1), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => script.SwapchainStatusNext(FakeBackendOperation.Present, SwapchainStatus.DeviceLost + 1, 0));
    }

    [Fact]
    public void SwapchainStatusNext_AlreadyPending_Throws()
    {
        _fixture.Script.SwapchainStatusNext(FakeBackendOperation.AcquireImage, SwapchainStatus.OutOfDate, 0);

        Assert.Throws<InvalidOperationException>(
            () => _fixture.Script.SwapchainStatusNext(FakeBackendOperation.AcquireImage, SwapchainStatus.SurfaceLost, 0));
    }

    [Fact]
    public void Operations_HaveOneMemberPerFakeMethod()
    {
        // Written from IGraphicsBackend's and IBackendDevice's methods (docs/design/graphics.md, "The backend interface"),
        // plus Dispose, in the note's order.
        string[] expected =
        [
            "EnumerateAdapters", "CreateDevice", "GetFormatSupport", "CreateBuffer", "CreateTexture", "CreateTextureView",
            "CreateSampler", "SetDebugName", "Destroy", "CreateSurface", "DestroySurface", "CreateSwapchain",
            "DestroySwapchain", "GetSwapchainImage", "AcquireImage", "Present", "WaitForFrame", "IsFrameComplete",
            "BeginFrame", "Execute", "TryReadTimestamps", "WaitIdle", "InjectValidationError", "Dispose",
        ];

        Assert.Equal(expected, Enum.GetNames<FakeBackendOperation>());
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(i, (int)Enum.Parse<FakeBackendOperation>(expected[i]));
        }
    }
}
