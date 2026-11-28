using System;
using System.Collections.Generic;
using System.Linq;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Internal;
using BrutalEngine.Graphics.Tests.Backend;
using BrutalEngine.Graphics.Tests.Device;
using BrutalEngine.TestUtilities;
using Xunit;
using static BrutalEngine.Graphics.Tests.Swapchains.SwapchainTestKit;
using Op = BrutalEngine.TestUtilities.FakeBackendOperation;

namespace BrutalEngine.Graphics.Tests.DeviceLoss;

/// <summary><c>GraphicsDevice.Recover</c>: owners, stale handles, swapchains, statistics and failures (docs/design/graphics.md section 12).</summary>
public sealed class RecoverTests : IDisposable
{
    private readonly DeviceFixture _fixture = new();
    private readonly List<string> _log = [];

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Owners_CalledInOrder_HandlesStale()
    {
        var rig = new LossRig(_fixture);
        GraphicsDevice device = rig.Device;
        FakeBackendDevice old = rig.Backend;
        TextureViewHandle view = device.CreateTextureView(new TextureViewDescription { Texture = rig.Texture }).Value;
        SamplerHandle sampler = device.CreateSampler(new SamplerDescription()).Value;
        TextureViewHandle defaultView = device.GetDefaultView(rig.Texture);
        rig.Frame();
        SwapchainImage image = device.GetSwapchainImage(rig.Swapchain, 0);
        var a = new RecordingOwner("a", _log);
        var b = new RecordingOwner("b", _log);
        var c = new RecordingOwner("c", _log);
        _ = device.RegisterOwner(a);
        _ = device.RegisterOwner(b);
        device.RegisterOwner(new RecordingOwner("gone", _log)).Dispose();
        _ = device.RegisterOwner(c);
        // While told of the loss, the old backend device is still there; once recreated, the device works again.
        b.WhenLost = _ => Assert.False(old.IsDisposed);
        BufferHandle remade = default;
        b.WhenRecreated = d => remade = d.CreateBuffer(new BufferDescription { Size = 16, Usage = BufferUsage.CopyDestination }).Value;
        Lose(device);

        Result result = device.Recover();

        Assert.True(result.IsSuccess);
        Assert.Equal(["lost c", "lost b", "lost a", "recreated a", "recreated b", "recreated c"], _log);
        Assert.False(device.IsLost);
        Assert.True(old.IsDisposed);
        Assert.NotSame(old, _fixture.BackendDevice);
        // Every old resource handle is stale, swapchain images included; the owner's new buffer is live.
        Assert.Throws<StaleHandleException>(() => device.Destroy(rig.Buffer));
        Assert.Throws<StaleHandleException>(() => device.GetDefaultView(rig.Texture));
        Assert.Throws<StaleHandleException>(() => device.Destroy(defaultView));
        Assert.Throws<StaleHandleException>(() => device.Destroy(view));
        Assert.Throws<StaleHandleException>(() => device.Destroy(sampler));
        Assert.Throws<StaleHandleException>(() => device.GetDefaultView(image.Texture));
        Assert.Equal((null, 0, false), (device.LostOperation, device.LostNativeCode, device.LostTimedOut));
        // The swapchain and the owner's buffer are the only objects; frames, creations and writes work.
        Assert.Equal(2, device.Statistics.LiveObjects);
        Assert.True(device.WriteBuffer(remade, 0, [1]).IsSuccess);
        Assert.Equal((FrameStatus.Ready, AcquireStatus.Recreated, FrameStatus.Ready), rig.Frame());
        Assert.True(device.CreateSampler(new SamplerDescription()).IsSuccess);
    }

    [Fact]
    public void Recover_DropsTheOldIdsWithoutBackendCalls()
    {
        var rig = new LossRig(_fixture);
        GraphicsDevice device = rig.Device;
        FakeBackendDevice old = rig.Backend;
        rig.Frame();
        // Objects queued for destruction, a pending write and a destroyed swapchain wait on the old device when it is lost.
        FakeWindow second = Window();
        SwapchainHandle other = device.CreateSwapchain(second, new SwapchainOptions()).Value;
        _ = device.BeginFrame(out CommandList commands);
        device.Destroy(device.CreateSampler(new SamplerDescription()).Value);
        Assert.True(commands.AllocateTransient(64, TransientUsage.Uniform, out _).IsSuccess);
        device.Destroy(other);
        device.Faults.InjectLoss(FaultSeam.Operation.Execute, 1, new EngineError(ErrorKind.Unavailable, "lost", -4));
        Assert.Equal(FrameStatus.DeviceLost, device.SubmitFrame(commands));
        Assert.True(device.WriteBuffer(rig.Buffer, 0, [1]).IsFailure);
        int calls = old.Calls.Count;

        Assert.True(device.Recover().IsSuccess);

        // The old device only got disposed; the new one never hears of an old id (the fake rejects unknown ids).
        Assert.Equal([Op.Dispose], old.Calls.Skip(calls).Select(call => call.Operation));
        // The destroyed swapchain's window gets a new one at once, without waiting for the old device's frame.
        Assert.True(device.CreateSwapchain(second, new SwapchainOptions()).IsSuccess);
        Assert.DoesNotContain(_fixture.BackendDevice.Calls, call => call.Operation == Op.WaitIdle);
        for (int frame = 0; frame < 4; frame++)
        {
            Assert.Equal(FrameStatus.Ready, rig.Frame().Submitted);
        }

        Assert.DoesNotContain(_fixture.BackendDevice.Calls, call => call.Operation == Op.Destroy);
    }

    [Fact]
    public void Swapchains_RecreatedInPlace()
    {
        var rig = new LossRig(_fixture);
        GraphicsDevice device = rig.Device;
        FakeWindow second = Window(320, 200);
        SwapchainHandle other = device.CreateSwapchain(second, new SwapchainOptions { DebugName = "second" }).Value;
        DebugLabel label = device.CreateDebugLabel("Main pass");
        rig.Frame();
        Lose(device);

        Assert.True(device.Recover().IsSuccess);

        // Each swapchain got a surface on the new device (the compatible window the selection surface); nothing else yet.
        FakeBackendDevice backend = _fixture.BackendDevice;
        Assert.Equal([Op.CreateSurface, Op.CreateSurface], Lifetime(backend).Select(call => call.Operation));
        // The same handles; the next acquire of each recreates it from nothing and reports it.
        _ = device.BeginFrame(out CommandList commands);
        Assert.Equal(AcquireStatus.Recreated, commands.AcquireSwapchainImage(rig.Swapchain, out SwapchainImage image));
        Assert.Equal(AcquireStatus.Recreated, commands.AcquireSwapchainImage(other, out SwapchainImage otherImage));
        Assert.Equal(FrameStatus.Ready, device.SubmitFrame(commands));
        Assert.Equal((640, 320), (image.Width, otherImage.Width));
        Assert.Equal(new SwapchainInfo(320, 200, TextureFormat.Bgra8UnormSrgb, PresentMode.Fifo, 3, true), device.GetSwapchainInfo(other));
        Assert.Equal(AcquireStatus.Acquired, rig.Frame().Acquired);
        Assert.Equal(2, backend.Calls.Count(call => call.Operation == Op.CreateSwapchain));
        // Labels survive, in the table the new backend reads too.
        Assert.Equal("Main pass", device.GetDebugLabelName(label));
        Assert.Equal("Main pass"u8.ToArray(), backend.Objects.GetLabel(label).ToArray());
        device.Destroy(other);
        device.Destroy(rig.Swapchain);
        Assert.True(device.WaitIdle().IsSuccess);
        Assert.Equal(0, backend.LiveObjectCount);
    }

    [Fact]
    public void Statistics_Survive()
    {
        GraphicsDevice device = _fixture.Create(_fixture.Options() with { Validation = true });
        FakeBackendDevice old = _fixture.BackendDevice;
        old.AddValidationMessages(3, 2);
        _ = device.BeginFrame(out CommandList commands);
        device.Faults.InjectLoss(FaultSeam.Operation.Execute, 1, new EngineError(ErrorKind.Unavailable, "lost", -4));
        _ = device.SubmitFrame(commands);

        Assert.True(device.Recover().IsSuccess);
        _fixture.BackendDevice.AddValidationMessages(1, 4);

        GraphicsStatistics statistics = device.Statistics;
        Assert.Equal((1L, 4L, 6L), (statistics.FramesSubmitted, statistics.ValidationErrors, statistics.ValidationWarnings));
        // A second recovery adds the second device's counts too.
        Lose(device);
        Assert.True(device.Recover().IsSuccess);
        Assert.Equal((4L, 6L), (device.Statistics.ValidationErrors, device.Statistics.ValidationWarnings));
    }

    [Fact]
    public void OwnerFailure_Returned()
    {
        GraphicsDevice device = _fixture.Create();
        var failure = new EngineError(ErrorKind.OutOfMemory, "atlas upload failed", -2);
        _ = device.RegisterOwner(new RecordingOwner("a", _log));
        _ = device.RegisterOwner(new RecordingOwner("b", _log) { Recreated = failure });
        _ = device.RegisterOwner(new RecordingOwner("c", _log));
        Lose(device);

        Result result = device.Recover();

        Assert.Equal(failure, result.Error);
        // The owners after the failing one aren't called; the device itself was recovered.
        Assert.Equal(["lost c", "lost b", "lost a", "recreated a", "recreated b"], _log);
        Assert.False(device.IsLost);
        Assert.Equal(FrameStatus.Ready, device.BeginFrame(out CommandList commands));
        Assert.Equal(FrameStatus.Ready, device.SubmitFrame(commands));
    }

    public static TheoryData<string> BackendFailures() =>
        ["EnumerateAdapters", "CreateDevice", "AdapterGone", "AdapterChanged", "NameChanged", "IndexChanged", "CapabilitiesChanged", "CreateSurface"];

    [Theory]
    [MemberData(nameof(BackendFailures))]
    public void BackendFailure_ReturnedAndDeviceStaysLost(string failure)
    {
        var rig = new LossRig(_fixture);
        GraphicsDevice device = rig.Device;
        FakeBackendDevice old = rig.Backend;
        _ = device.RegisterOwner(new RecordingOwner("owner", _log));
        Lose(device);
        var error = new EngineError(ErrorKind.Native, "injected", -3);
        GraphicsCapabilities capabilities = _fixture.Script.Capabilities;
        switch (failure)
        {
            case "EnumerateAdapters" or "CreateDevice" or "CreateSurface":
                _fixture.Script.FailNext(Enum.Parse<Op>(failure), error);
                break;
            case "AdapterGone":
                _fixture.Script.Adapters[0] = FakeBackendScript.CreateAdapter(0, "unplugged");
                break;
            case "AdapterChanged":
                // The first adapter is gone and Auto picks the other one: not this device's adapter.
                _fixture.Script.Adapters[0] = FakeBackendScript.CreateAdapter(0, "unplugged");
                _fixture.Script.Adapters.Add(FakeBackendScript.CreateAdapter(1));
                break;
            case "NameChanged":
                // The same index, another name.
                _fixture.Script.Adapters[0] = FakeBackendScript.CreateAdapter(0) with { Name = "Other GPU" };
                break;
            case "IndexChanged":
                // The same name, another index: adapter 0 is rejected and adapter 1 takes its name.
                _fixture.Script.Adapters[0] = FakeBackendScript.CreateAdapter(0, "unplugged");
                _fixture.Script.Adapters.Add(FakeBackendScript.CreateAdapter(1) with { Name = "Fake GPU 0" });
                break;
            default:
                _fixture.Script.Capabilities = capabilities with { Limits = capabilities.Limits with { MaxColorTargets = 4 } };
                break;
        }

        Result result = device.Recover();

        (ErrorKind kind, string message) = failure switch
        {
            "AdapterGone" => (ErrorKind.Unsupported, "Create: no suitable adapter."),
            "AdapterChanged" or "NameChanged" or "IndexChanged" or "CapabilitiesChanged" => (ErrorKind.NotFound, "Recover: adapter 0 'Fake GPU 0' is gone or has changed."),
            _ => (ErrorKind.Native, "injected"),
        };
        Assert.Equal(kind, result.Error.Kind);
        Assert.StartsWith(message, result.Error.Message, StringComparison.Ordinal);
        Assert.True(device.IsLost);
        Assert.Equal(["lost owner"], _log);
        Assert.True(old.IsDisposed);
        // A backend device made for the recovery doesn't outlive the failure.
        Assert.True(_fixture.BackendDevice.IsDisposed);
        // Without a backend device nothing reaches one: frames, destruction and the format query.
        Assert.Throws<InvalidOperationException>(() => device.GetFormatSupport(TextureFormat.Rgba8Unorm));
        Assert.Equal((FrameStatus.DeviceLost, AcquireStatus.DeviceLost, FrameStatus.DeviceLost), rig.Frame());
        Assert.Equal(0, device.Statistics.ValidationErrors);

        // Once the backend is back, a later call recovers, dropping what was destroyed in between.
        (_fixture.Script.Capabilities, _fixture.Script.Adapters[0]) = (capabilities, FakeBackendScript.CreateAdapter(0));
        _fixture.Script.Adapters.RemoveRange(1, _fixture.Script.Adapters.Count - 1);
        device.Destroy(rig.Swapchain);
        Assert.True(device.Recover().IsSuccess);
        Assert.Equal(1, old.DisposeCalls);
        Assert.Equal(["lost owner", "lost owner", "recreated owner"], _log);
        Assert.Equal(FrameStatus.Ready, device.BeginFrame(out CommandList commands));
        Assert.Equal(FrameStatus.Ready, device.SubmitFrame(commands));
        Assert.DoesNotContain(_fixture.BackendDevice.Calls, call => call.Operation is Op.DestroySurface or Op.DestroySwapchain);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Statistics_AfterFailedRecoveryOrAbandon_CountTheOldDeviceOnce(bool timedOut)
    {
        GraphicsDevice device = _fixture.Create(_fixture.Options() with { Validation = true });
        FakeBackendDevice old = _fixture.BackendDevice;
        old.AddValidationMessages(3, 2);
        device.Faults.InjectLoss(
            timedOut ? FaultSeam.Operation.WaitForFrame : FaultSeam.Operation.Execute,
            1,
            new EngineError(timedOut ? ErrorKind.Timeout : ErrorKind.Unavailable, "lost", 2));
        _ = device.BeginFrame(out CommandList commands);
        Assert.Equal(FrameStatus.DeviceLost, device.SubmitFrame(commands));

        // Twice: a failed recovery (or the abandon) and a later call that fails again.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!timedOut)
            {
                _fixture.Script.FailNext(Op.CreateDevice, new EngineError(ErrorKind.Native, "injected", -3));
            }

            Assert.True(device.Recover().IsFailure);
            Assert.Equal((3L, 2L), (device.Statistics.ValidationErrors, device.Statistics.ValidationWarnings));
        }

        Assert.Equal(timedOut, !old.IsDisposed);
        device.Dispose();
        // An abandoned device's memory is freed by the test, as the process would at exit.
        old.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BackendGoneBeforeAnyFrame_DestroyMakesNoBackendCall(bool timedOut)
    {
        var rig = new LossRig(_fixture);
        // Lost before any frame, so a destroyed object would otherwise be destroyed at once.
        rig.Device.Faults.InjectLoss(FaultSeam.Operation.CreateBuffer, 1, new EngineError(timedOut ? ErrorKind.Timeout : ErrorKind.Unavailable, "lost", 2));
        Assert.True(rig.Creation("CreateBuffer", default).IsFailure);
        Assert.Equal((0L, timedOut), (rig.Device.FrameNumber, rig.Device.LostTimedOut));
        if (!timedOut)
        {
            _fixture.Script.FailNext(Op.CreateDevice, new EngineError(ErrorKind.Native, "injected", -3));
        }

        Assert.True(rig.Device.Recover().IsFailure);
        int calls = rig.Backend.Calls.Count;

        rig.Device.Destroy(rig.Swapchain);

        // The old backend device, disposed or hung, is never called.
        Assert.Equal(calls, rig.Backend.Calls.Count);
        rig.Device.Dispose();
        Assert.Equal(calls, rig.Backend.Calls.Count);
        rig.Backend.Dispose();
    }

    [Fact]
    public void OwnersUnregisteredDuringRecovery_NotCalledAgain()
    {
        GraphicsDevice device = _fixture.Create();
        GpuOwnerRegistration a = device.RegisterOwner(new RecordingOwner("a", _log));
        GpuOwnerRegistration? b = null;
        b = device.RegisterOwner(new RecordingOwner("b", _log) { WhenLost = _ => b!.Dispose() });
        _ = device.RegisterOwner(new RecordingOwner("c", _log) { WhenLost = _ => a.Dispose() });
        Lose(device);

        Assert.True(device.Recover().IsSuccess);

        // c unregistered a before a was told of the loss; b unregistered itself, so it isn't told of the recreation.
        Assert.Equal(["lost c", "lost b", "recreated c"], _log);
        Assert.Equal(1, device.RegisteredOwnerCount);
    }

    [Fact]
    public void FailedRecovery_DisposeMakesNoBackendCall()
    {
        var rig = new LossRig(_fixture);
        Lose(rig.Device);
        _fixture.Script.FailNext(Op.CreateDevice, new EngineError(ErrorKind.Native, "injected", -3));
        Assert.True(rig.Device.Recover().IsFailure);
        int calls = rig.Backend.Calls.Count;

        rig.Device.Dispose();

        Assert.Equal(calls, rig.Backend.Calls.Count);
        Assert.DoesNotContain(_fixture.Entries(), entry => entry.Message.StartsWith("Device abandoned", StringComparison.Ordinal));
    }

    [Fact]
    public void Misuse_Throws()
    {
        GraphicsDevice device = _fixture.Create();

        InvalidOperationException notLost = Assert.Throws<InvalidOperationException>(() => device.Recover());

        Assert.Equal("Recover: the device isn't lost.", notLost.Message);
        Lose(device);
        int calls = _fixture.BackendDevice.Calls.Count;
        _ = device.BeginFrame(out CommandList commands);
        InvalidOperationException inFrame = Assert.Throws<InvalidOperationException>(() => device.Recover());
        Assert.Equal("Recover: frame 1 is open; submit it first.", inFrame.Message);
        _ = device.SubmitFrame(commands);
        // Off the owner thread: refused before anything happens.
        Assert.IsType<WrongThreadException>(GraphicsDeviceThreadTests.OnOtherThread(() => device.Recover()));
        Assert.Equal(calls, _fixture.BackendDevice.Calls.Count);
        Assert.True(device.IsLost);
    }

    [Fact]
    public void OwnerCallingRecover_Throws()
    {
        GraphicsDevice device = _fixture.Create();
        Exception? nested = null;
        var owner = new RecordingOwner("owner", _log) { WhenLost = d => nested = Record.Exception(() => d.Recover()) };
        _ = device.RegisterOwner(owner);
        Lose(device);

        Assert.True(device.Recover().IsSuccess);

        Assert.Equal("Recover: a recovery is already running.", Assert.IsType<InvalidOperationException>(nested).Message);
        Assert.Equal(["lost owner", "recreated owner"], _log);
    }

    [Fact]
    public void OwnerException_PropagatesAndALaterCallRecovers()
    {
        GraphicsDevice device = _fixture.Create();
        var thrown = new InvalidOperationException("owner failed");
        var owner = new RecordingOwner("owner", _log) { WhenLost = _ => throw thrown };
        _ = device.RegisterOwner(owner);
        Lose(device);

        Assert.Same(thrown, Assert.Throws<InvalidOperationException>(() => device.Recover()));

        Assert.True(device.IsLost);
        owner.WhenLost = null;
        Assert.True(device.Recover().IsSuccess);
        Assert.Equal(["lost owner", "lost owner", "recreated owner"], _log);
    }

    // Loses the device at its next frame's submit, through the fault seam.
    private static void Lose(GraphicsDevice device)
    {
        device.Faults.InjectLoss(FaultSeam.Operation.Execute, 1, new EngineError(ErrorKind.Unavailable, "lost", -4));
        _ = device.BeginFrame(out CommandList commands);
        Assert.Equal(FrameStatus.DeviceLost, device.SubmitFrame(commands));
    }
}
