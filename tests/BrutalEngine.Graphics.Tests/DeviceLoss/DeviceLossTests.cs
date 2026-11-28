using System;
using System.Collections.Generic;
using System.Linq;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Internal;
using BrutalEngine.Graphics.Tests.Backend;
using BrutalEngine.Graphics.Tests.Device;
using Xunit;
using static BrutalEngine.Graphics.Tests.Swapchains.SwapchainTestKit;
using Op = BrutalEngine.TestUtilities.FakeBackendOperation;

namespace BrutalEngine.Graphics.Tests.DeviceLoss;

/// <summary>
/// Device-loss detection through the fault seam and the backend's own statuses, the DeviceLost event, and what a lost
/// device still does (docs/design/graphics.md section 12).
/// </summary>
public sealed class DeviceLossTests : IDisposable
{
    private const int LossCode = -4;

    private readonly DeviceFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    /// <summary>Every scenario with a reported loss, and every one that returns a Result with a timeout too.</summary>
    public static TheoryData<string, bool> LossScenarios()
    {
        TheoryData<string, bool> rows = [];
        foreach (string scenario in LossKit.Scenarios.Keys)
        {
            rows.Add(scenario, false);
            if (scenario is not ("Present" or "AcquireImage"))
            {
                rows.Add(scenario, true);
            }
        }

        return rows;
    }

    [Fact]
    public void Scenarios_CoverEverySeamOperation()
    {
        Assert.Equal(
            Enum.GetValues<FaultSeam.Operation>(),
            LossKit.Scenarios.Values.Select(scenario => scenario.Operation).Distinct().Order());
    }

    [Theory]
    [MemberData(nameof(LossScenarios))]
    public void InjectedLoss_EachOperation(string scenario, bool timedOut)
    {
        var rig = new LossRig(_fixture);
        string detected = LossKit.Scenarios[scenario].Detected;
        var loss = new EngineError(timedOut ? ErrorKind.Timeout : ErrorKind.Unavailable, "Injected loss.", timedOut ? 7 : LossCode);

        (object returned, int backendCalls) = rig.Run(scenario, loss);

        // The seam replaced the backend call: it never reached the backend.
        Assert.Equal(0, backendCalls);
        switch (returned)
        {
            case FrameStatus frame:
                Assert.Equal(FrameStatus.DeviceLost, frame);
                break;
            case AcquireStatus acquire:
                Assert.Equal(AcquireStatus.DeviceLost, acquire);
                break;
            case Result result when scenario == "WaitIdle":
                // WaitIdle returns the backend's failure unchanged.
                Assert.Equal(loss, result.Error);
                break;
            case Result result:
                // A creation or write reports any loss as Unavailable, with the loss's native code.
                Assert.Equal((ErrorKind.Unavailable, loss.NativeCode), (result.Error.Kind, result.Error.NativeCode));
                Assert.Equal(timedOut ? $"{detected}: the device is lost (Injected loss.)." : "Injected loss.", Unqualified(result.Error.Message));
                break;
            default:
                Assert.Fail($"Unexpected result {returned}.");
                break;
        }

        Assert.True(rig.Device.IsLost);
        Assert.Equal((detected, loss.NativeCode, timedOut), (rig.Device.LostOperation, rig.Device.LostNativeCode, rig.Device.LostTimedOut));
        Assert.Equal([(rig.Device, detected, loss.NativeCode, timedOut)], rig.Events);
        Assert.Equal([true], rig.LostInHandler);
    }

    [Fact]
    public void InjectedLoss_NthCallOnly()
    {
        var rig = new LossRig(_fixture);
        rig.Device.Faults.InjectLoss(FaultSeam.Operation.Execute, 3, new EngineError(ErrorKind.Unavailable, "third", LossCode));

        // Other operations don't count toward the occurrence: two full frames with acquires, writes and creations.
        for (int frame = 0; frame < 2; frame++)
        {
            Assert.True(rig.Device.WriteBuffer(rig.Buffer, 0, [1]).IsSuccess);
            Assert.Equal((FrameStatus.Ready, AcquireStatus.Acquired, FrameStatus.Ready), rig.Frame());
            Assert.True(rig.Device.WaitIdle().IsSuccess);
        }

        Assert.Empty(rig.Events);
        Assert.Equal((FrameStatus.Ready, AcquireStatus.Acquired, FrameStatus.DeviceLost), rig.Frame());
        Assert.Equal(2, rig.Backend.Calls.Count(call => call.Operation == Op.Execute));
        Assert.Equal([(rig.Device, "SubmitFrame", LossCode, false)], rig.Events);
    }

    [Fact]
    public void Event_RaisedOnce()
    {
        var rig = new LossRig(_fixture);
        long frameInHandler = -1;
        rig.Device.DeviceLost += (_, _) => frameInHandler = rig.Device.FrameNumber;
        Assert.Equal((FrameStatus.Ready, AcquireStatus.Acquired, FrameStatus.Ready), rig.Frame());
        rig.Device.Faults.InjectLoss(FaultSeam.Operation.Execute, 1, new EngineError(ErrorKind.Unavailable, "lost", LossCode));

        Assert.Equal((FrameStatus.Ready, AcquireStatus.Acquired, FrameStatus.DeviceLost), rig.Frame());

        // Raised at the end of SubmitFrame: the frame was already closed.
        Assert.Equal(2, frameInHandler);
        // Every later detecting call meets the lost device, and none raises it again.
        for (int frame = 0; frame < 3; frame++)
        {
            Assert.Equal((FrameStatus.DeviceLost, AcquireStatus.DeviceLost, FrameStatus.DeviceLost), rig.Frame());
        }

        // WaitIdle refuses with the loss's native code, as the creations and writes do.
        Result idle = rig.Device.WaitIdle();
        Assert.Equal((ErrorKind.Unavailable, LossCode), (idle.Error.Kind, idle.Error.NativeCode));
        Assert.Equal(ErrorKind.Unavailable, rig.Device.CreateBuffer(new BufferDescription { Size = 4, Usage = BufferUsage.Vertex }).Error.Kind);
        Assert.Equal(ErrorKind.Unavailable, rig.Device.WriteBuffer(rig.Buffer, 0, [1]).Error.Kind);
        Assert.Single(rig.Events);
        Assert.Equal((rig.Device, "SubmitFrame", LossCode, false), rig.Events[0]);
    }

    [Theory]
    [InlineData("WaitForFrame")]
    [InlineData("Execute")]
    [InlineData("CreateBuffer")]
    [InlineData("WriteBuffer")]
    public void Event_HandlerException_PropagatesAndDeviceStaysConsistent(string scenario)
    {
        var rig = new LossRig(_fixture);
        var thrown = new InvalidOperationException("handler failed");
        rig.Device.DeviceLost += (_, _) => throw thrown;
        long frames = rig.Device.FrameNumber;

        Exception caught = Assert.ThrowsAny<Exception>(() => rig.Run(scenario, new EngineError(ErrorKind.Unavailable, "lost", LossCode)));

        Assert.Same(thrown, caught);
        Assert.True(rig.Device.IsLost);
        // A frame detected at BeginFrame wasn't opened; one detected at SubmitFrame was closed first.
        // (The WaitForFrame scenario submits one frame before the one it fails to open.)
        Assert.Equal(scenario is "WaitForFrame" or "Execute" ? frames + 1 : frames, rig.Device.FrameNumber);
        // Frames still begin and submit, and the event isn't raised again.
        Assert.Equal((FrameStatus.DeviceLost, AcquireStatus.DeviceLost, FrameStatus.DeviceLost), rig.Frame());
        Assert.Single(rig.Events);
    }

    [Fact]
    public void AfterLoss_FramesCompleteAndCreatesFail()
    {
        var rig = new LossRig(_fixture);
        GraphicsDevice device = rig.Device;
        TextureViewHandle view = device.CreateTextureView(new TextureViewDescription { Texture = rig.Texture }).Value;
        Assert.Equal((FrameStatus.Ready, AcquireStatus.Acquired, FrameStatus.Ready), rig.Frame());
        device.Faults.InjectLoss(FaultSeam.Operation.WaitForFrame, 1, new EngineError(ErrorKind.Unavailable, "lost", LossCode));
        Assert.Equal(FrameStatus.DeviceLost, device.BeginFrame(out CommandList lost));
        Assert.Equal(FrameStatus.DeviceLost, device.SubmitFrame(lost));
        int calls = rig.Backend.Calls.Count;
        long frames = device.FrameNumber;

        // Frames still complete, and every encoder call does nothing: no backend call, nothing recorded or reserved.
        for (int frame = 0; frame < 3; frame++)
        {
            Assert.Equal(FrameStatus.DeviceLost, device.BeginFrame(out CommandList commands));
            Assert.Equal((AcquireStatus.DeviceLost, default(SwapchainImage)), (commands.AcquireSwapchainImage(rig.Swapchain, out SwapchainImage image), image));
            commands.CopyBufferToBuffer(new BufferCopy { Source = rig.Buffer, Destination = rig.Buffer, DestinationOffset = 128, Size = 16 });
            commands.CopyBufferToTexture(new TextureBufferCopy { Buffer = rig.Buffer, BytesPerRow = 256, Texture = new(rig.Texture), Region = new(0, 0, 1, 1) });
            Result transient = commands.AllocateTransient(16, TransientUsage.Uniform, out TransientAllocation allocation);
            Assert.Equal((ErrorKind.Unavailable, true), (transient.Error.Kind, allocation.Buffer.IsNull));
            Assert.Equal(FrameStatus.DeviceLost, device.SubmitFrame(commands));
        }

        Assert.Equal(frames + 3, device.FrameNumber);

        // Every creation and write fails with Unavailable and the loss's native code, without reaching the backend.
        string[] apis = ["CreateBuffer", "CreateTexture", "CreateTextureView", "CreateSampler", "CreateSwapchain", "WriteBuffer", "WriteTexture"];
        List<Result> failures = [.. apis.Select(api => rig.Creation(api, default))];
        Assert.Equal(
            apis.Select(api => (ErrorKind.Unavailable, $"GraphicsDevice.{api}: the device is lost.", LossCode)),
            failures.Select(result => (result.Error.Kind, result.Error.Message, result.Error.NativeCode)));
        Assert.Equal(calls, rig.Backend.Calls.Count);
        Assert.Single(rig.Events);

        // Destroying still works: the handles go stale, and the objects wait for the device's disposal.
        device.Destroy(view);
        device.Destroy(rig.Texture);
        device.Destroy(rig.Buffer);
        device.Destroy(rig.Swapchain);
        Assert.Throws<StaleHandleException>(() => device.GetDefaultView(rig.Texture));
        device.ThrowIfLeaks();
    }

    /// <summary>Every creation and write, with every error kind its backend call can fail with.</summary>
    public static TheoryData<string, ErrorKind> CreationFailures()
    {
        TheoryData<string, ErrorKind> rows = [];
        foreach (string scenario in LossKit.Creations)
        {
            foreach (ErrorKind kind in Enum.GetValues<ErrorKind>())
            {
                rows.Add(scenario, kind);
            }
        }

        return rows;
    }

    [Theory]
    [MemberData(nameof(CreationFailures))]
    public void BackendCreationFailure_OnlyLossKindsAreLossAndComeBackUnavailable(string scenario, ErrorKind kind)
    {
        var rig = new LossRig(_fixture);
        FaultSeam.Operation operation = LossKit.Scenarios[scenario].Operation;
        var failure = new EngineError(kind, "backend failure", 21);
        _fixture.Script.FailNext(LossKit.Fake(operation), failure);

        Result result = Creation(rig, scenario);

        bool loss = kind is ErrorKind.Unavailable or ErrorKind.Timeout;
        string detected = LossKit.Scenarios[scenario].Detected;
        Assert.Equal(loss, rig.Device.IsLost);
        if (loss)
        {
            Assert.Equal((ErrorKind.Unavailable, 21), (result.Error.Kind, result.Error.NativeCode));
            Assert.Equal(kind == ErrorKind.Timeout ? $"{detected}: the device is lost (backend failure)." : "backend failure", Unqualified(result.Error.Message));
            Assert.Equal((detected, 21, kind == ErrorKind.Timeout), (rig.Device.LostOperation, rig.Device.LostNativeCode, rig.Device.LostTimedOut));
            Assert.Equal([(rig.Device, detected, 21, kind == ErrorKind.Timeout)], rig.Events);
        }
        else
        {
            // Anything else is returned unchanged and loses nothing.
            Assert.Equal(failure, result.Error);
            Assert.Empty(rig.Events);
            Assert.Equal((FrameStatus.Ready, AcquireStatus.Acquired, FrameStatus.Ready), rig.Frame());
        }
    }

    [Theory]
    [MemberData(nameof(CreationScenarios))]
    public void AfterTimedOutLoss_CreatesAndWritesAreUnavailable(string scenario)
    {
        var rig = new LossRig(_fixture);
        rig.Device.Faults.InjectLoss(FaultSeam.Operation.WaitForFrame, 1, new EngineError(ErrorKind.Timeout, "hung", 2));
        Assert.Equal((FrameStatus.DeviceLost, AcquireStatus.DeviceLost, FrameStatus.DeviceLost), rig.Frame());
        Assert.True(rig.Device.LostTimedOut);
        // A backend that remembers the timeout would answer Timeout; the device never asks it.
        FaultSeam.Operation operation = LossKit.Scenarios[scenario].Operation;
        _fixture.Script.FailNext(LossKit.Fake(operation), new EngineError(ErrorKind.Timeout, "remembered timeout", 2));
        int calls = rig.Backend.Calls.Count;

        Result result = Creation(rig, scenario);

        Assert.Equal((ErrorKind.Unavailable, 2), (result.Error.Kind, result.Error.NativeCode));
        Assert.EndsWith(": the device is lost.", result.Error.Message, StringComparison.Ordinal);
        Assert.Equal(calls, rig.Backend.Calls.Count);
        Assert.Equal([(rig.Device, "BeginFrame", 2, true)], rig.Events);
    }

    public static TheoryData<string> CreationScenarios() => [.. LossKit.Creations];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateSwapchain_RetiredSwapchainWaitDetectsLoss_Unavailable(bool timedOut)
    {
        var rig = new LossRig(_fixture);
        FakeWindow window = RetireSwapchain(rig);
        var loss = new EngineError(timedOut ? ErrorKind.Timeout : ErrorKind.Unavailable, "hung", timedOut ? 7 : LossCode);
        rig.Device.Faults.InjectLoss(FaultSeam.Operation.WaitIdle, 1, loss);
        int surfaces = rig.Backend.Calls.Count(call => call.Operation == Op.CreateSurface);

        var result = rig.Device.CreateSwapchain(window, new SwapchainOptions()).ToResult();

        // The wait detected the loss and raised the event as "WaitIdle"; the creation reports the loss as any creation
        // does (Unavailable with its native code, never Timeout) and creates nothing.
        Assert.Equal((ErrorKind.Unavailable, loss.NativeCode), (result.Error.Kind, result.Error.NativeCode));
        Assert.Equal(timedOut ? "CreateSwapchain: the device is lost (hung)." : "hung", Unqualified(result.Error.Message));
        Assert.Equal(("WaitIdle", loss.NativeCode, timedOut), (rig.Device.LostOperation, rig.Device.LostNativeCode, rig.Device.LostTimedOut));
        Assert.Equal([(rig.Device, "WaitIdle", loss.NativeCode, timedOut)], rig.Events);
        Assert.Equal(surfaces, rig.Backend.Calls.Count(call => call.Operation == Op.CreateSurface));
    }

    [Fact]
    public void CreateSwapchain_RetiredSwapchainWaitFailsWithoutLoss_ReturnedUnchanged()
    {
        var rig = new LossRig(_fixture);
        FakeWindow window = RetireSwapchain(rig);
        var failure = new EngineError(ErrorKind.Native, "driver failure", 9);
        _fixture.Script.FailNext(Op.WaitIdle, failure);

        var result = rig.Device.CreateSwapchain(window, new SwapchainOptions()).ToResult();

        Assert.Equal(failure, result.Error);
        Assert.False(rig.Device.IsLost);
        Assert.Empty(rig.Events);
    }

    [Fact]
    public void CreateSwapchain_AfterTimedOutLoss_RefusedBeforeTheRetiredSwapchainWait()
    {
        var rig = new LossRig(_fixture);
        FakeWindow window = RetireSwapchain(rig);
        rig.Device.Faults.InjectLoss(FaultSeam.Operation.WaitForFrame, 1, new EngineError(ErrorKind.Timeout, "hung", 2));
        Assert.Equal((FrameStatus.DeviceLost, AcquireStatus.DeviceLost, FrameStatus.DeviceLost), rig.Frame());
        // The destroyed swapchain's frame never completed, so the window's next swapchain would have to wait for it.
        Assert.Equal(1, rig.Device.RetiringSwapchainCount);
        int calls = rig.Backend.Calls.Count;

        var result = rig.Device.CreateSwapchain(window, new SwapchainOptions()).ToResult();

        // Refused as every creation on a lost device is: the loss's native code, no wait, no backend call.
        Assert.Equal(
            (ErrorKind.Unavailable, "GraphicsDevice.CreateSwapchain: the device is lost.", 2),
            (result.Error.Kind, result.Error.Message, result.Error.NativeCode));
        Assert.Equal(calls, rig.Backend.Calls.Count);
        Assert.Equal([(rig.Device, "BeginFrame", 2, true)], rig.Events);
    }

    // A second window whose swapchain was destroyed in a submitted frame that hasn't completed: the window's next
    // CreateSwapchain waits for the GPU first.
    private static FakeWindow RetireSwapchain(LossRig rig)
    {
        FakeWindow window = Window();
        SwapchainHandle old = rig.Device.CreateSwapchain(window, new SwapchainOptions { DebugName = "retired" }).Value;
        Assert.Equal(FrameStatus.Ready, rig.Device.BeginFrame(out CommandList commands));
        rig.Device.Destroy(old);
        Assert.Equal(FrameStatus.Ready, rig.Device.SubmitFrame(commands));
        Assert.Equal(1, rig.Device.RetiringSwapchainCount);
        return window;
    }

    // A creation or write, in a frame for a transient allocation.
    private static Result Creation(LossRig rig, string scenario)
    {
        CommandList commands = default;
        _ = scenario == "AllocateTransient" ? rig.Device.BeginFrame(out commands) : FrameStatus.Ready;
        Result result = rig.Creation(scenario, commands);
        _ = scenario == "AllocateTransient" ? rig.Device.SubmitFrame(commands) : FrameStatus.Ready;
        return result;
    }

    private static string Unqualified(string message) =>
        message.Replace("GraphicsDevice.", string.Empty, StringComparison.Ordinal).Replace("CommandList.", string.Empty, StringComparison.Ordinal);
}
