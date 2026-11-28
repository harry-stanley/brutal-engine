using System;
using System.Collections.Generic;
using System.Linq;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Internal;
using BrutalEngine.Graphics.Tests.Device;
using Xunit;

namespace BrutalEngine.Graphics.Tests.DeviceLoss;

/// <summary><c>GraphicsDevice.RegisterOwner</c> and its registration token (docs/design/graphics.md section 12).</summary>
public sealed class OwnerRegistrationTests : IDisposable
{
    private readonly DeviceFixture _fixture = new();
    private readonly List<string> _log = [];

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Dispose_Idempotent()
    {
        GraphicsDevice device = _fixture.Create();
        var first = new RecordingOwner("first", _log);
        var second = new RecordingOwner("second", _log);
        GpuOwnerRegistration a = device.RegisterOwner(first);
        GpuOwnerRegistration b = device.RegisterOwner(second);
        // The same owner twice is two registrations.
        GpuOwnerRegistration c = device.RegisterOwner(first);
        Assert.Equal(3, device.RegisteredOwnerCount);

        b.Dispose();
        b.Dispose();

        // Recovery calls each live registration once: the first owner twice, the unregistered one never.
        Assert.Equal(2, device.RegisteredOwnerCount);
        LoseAndRecover(device);
        Assert.Equal(["lost first", "lost first", "recreated first", "recreated first"], _log);
        a.Dispose();
        Assert.Equal(1, device.RegisteredOwnerCount);
        a.Dispose();
        b.Dispose();
        Assert.Equal(1, device.RegisteredOwnerCount);
        c.Dispose();
        Assert.Equal(0, device.RegisteredOwnerCount);
        _log.Clear();
        LoseAndRecover(device);
        Assert.Empty(_log);
    }

    [Fact]
    public void RegisterOwner_Null_Throws()
    {
        GraphicsDevice device = _fixture.Create();

        Assert.Throws<ArgumentNullException>(() => device.RegisterOwner(null!));

        Assert.Equal(0, device.RegisteredOwnerCount);
    }

    [Fact]
    public void RegisterOwner_OtherThread_ThrowsAndRegistersNothing()
    {
        GraphicsDevice device = _fixture.Create();

        Exception? thrown = GraphicsDeviceThreadTests.OnOtherThread(() => device.RegisterOwner(new RecordingOwner("other", _log)));

        WrongThreadException wrong = Assert.IsType<WrongThreadException>(thrown);
        Assert.Contains("GraphicsDevice.RegisterOwner", wrong.Message, StringComparison.Ordinal);
        Assert.Equal(0, device.RegisteredOwnerCount);
    }

    [Fact]
    public void RegisterOwner_AfterDispose_Throws()
    {
        GraphicsDevice device = _fixture.Create();
        device.Dispose();

        Assert.Throws<ObjectDisposedException>(() => device.RegisterOwner(new RecordingOwner("late", _log)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegistrationDispose_OtherThread_ThrowsAndKeepsState(bool alreadyDisposed)
    {
        GraphicsDevice device = _fixture.Create();
        GpuOwnerRegistration registration = device.RegisterOwner(new RecordingOwner("owner", _log));
        if (alreadyDisposed)
        {
            registration.Dispose();
        }

        Exception? thrown = GraphicsDeviceThreadTests.OnOtherThread(registration.Dispose);

        WrongThreadException wrong = Assert.IsType<WrongThreadException>(thrown);
        Assert.Contains("GpuOwnerRegistration.Dispose", wrong.Message, StringComparison.Ordinal);
        Assert.Equal(alreadyDisposed ? 0 : 1, device.RegisteredOwnerCount);
        registration.Dispose();
        Assert.Equal(0, device.RegisteredOwnerCount);
    }

    [Fact]
    public void RegistrationDispose_AfterDeviceDispose_DoesNothing()
    {
        GraphicsDevice device = _fixture.Create();
        GpuOwnerRegistration registration = device.RegisterOwner(new RecordingOwner("owner", _log));
        device.Dispose();

        registration.Dispose();
        registration.Dispose();

        Assert.Empty(_log);
    }

    [Fact]
    public void RegistrationDispose_AllocatesNothing()
    {
        GraphicsDevice device = _fixture.Create();
        GpuOwnerRegistration[] registrations = [.. Enumerable.Range(0, 64).Select(i => device.RegisterOwner(new RecordingOwner($"owner {i}", _log)))];

        // Warm-up outside the window: the process's first List<T>.Remove for the type creates and caches its equality
        // comparer (88 bytes, measured), whichever test runs first.
        device.RegisterOwner(new RecordingOwner("warm-up", _log)).Dispose();

        // Each one unregistered, then again: the first call finds and removes it, the second finds nothing.
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (GpuOwnerRegistration registration in registrations)
        {
            registration.Dispose();
            registration.Dispose();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(0, device.RegisteredOwnerCount);
    }

    private static void LoseAndRecover(GraphicsDevice device)
    {
        device.Faults.InjectLoss(FaultSeam.Operation.Execute, 1, new EngineError(ErrorKind.Unavailable, "lost", -4));
        _ = device.BeginFrame(out CommandList commands);
        Assert.Equal(FrameStatus.DeviceLost, device.SubmitFrame(commands));
        Assert.True(device.Recover().IsSuccess);
    }
}
