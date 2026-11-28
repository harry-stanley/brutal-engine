using System;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Internal;
using BrutalEngine.Graphics.Tests.Device;
using Xunit;
using Op = BrutalEngine.TestUtilities.FakeBackendOperation;

namespace BrutalEngine.Graphics.Tests.DeviceLoss;

/// <summary>The front end's seam for the negative validation canary (docs/design/graphics.md sections 12 and 15).</summary>
public sealed class CanaryTests : IDisposable
{
    private readonly DeviceFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void WithoutValidation_ThrowsBeforeBackend()
    {
        GraphicsDevice device = _fixture.Create();
        int calls = _fixture.BackendDevice.Calls.Count;

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(device.InjectValidationError);

        Assert.Contains("created without Validation", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(calls, _fixture.BackendDevice.Calls.Count);
    }

    [Fact]
    public void WithValidation_NextFrameCountsOneError()
    {
        GraphicsDevice device = _fixture.Create(_fixture.Options() with { Validation = true });

        device.InjectValidationError();
        _ = device.BeginFrame(out CommandList commands);
        _ = device.SubmitFrame(commands);

        Assert.Single(_fixture.BackendDevice.Calls, call => call.Operation == Op.InjectValidationError);
        Assert.Equal(1, device.Statistics.ValidationErrors);
    }

    [Fact]
    public void Misuse_ThrowsBeforeBackend()
    {
        GraphicsDevice device = _fixture.Create(_fixture.Options() with { Validation = true });
        Assert.IsType<WrongThreadException>(GraphicsDeviceThreadTests.OnOtherThread(device.InjectValidationError));
        device.Faults.InjectLoss(FaultSeam.Operation.WaitForFrame, 1, new EngineError(ErrorKind.Unavailable, "lost", -4));
        _ = device.BeginFrame(out CommandList commands);
        _ = device.SubmitFrame(commands);

        Assert.Equal("InjectValidationError: the device is lost.", Assert.Throws<InvalidOperationException>(device.InjectValidationError).Message);

        device.Dispose();
        Assert.Throws<ObjectDisposedException>(device.InjectValidationError);
        Assert.DoesNotContain(_fixture.BackendDevice.Calls, call => call.Operation == Op.InjectValidationError);
    }
}
