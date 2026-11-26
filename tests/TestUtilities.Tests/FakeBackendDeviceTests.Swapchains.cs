using System;
using System.Collections.Generic;
using BrutalEngine.Core;
using BrutalEngine.Graphics;
using BrutalEngine.Graphics.Backend;
using Xunit;

namespace BrutalEngine.TestUtilities.Tests;

/// <summary>The fake backend device's surfaces and swapchains.</summary>
public sealed partial class FakeBackendDeviceTests
{
    private static readonly string[] _swapchainViolations =
    [
        "DestroySurface unknown id",
        "DestroySurface twice",
        "DestroySurface with a live swapchain",
        "DestroySurface with a retired swapchain",
        "CreateSurface twice for one window",
        "CreateSurface after Dispose",
        "CreateSwapchain on an unknown surface",
        "CreateSwapchain on a destroyed surface",
        "CreateSwapchain with another window's source",
        "CreateSwapchain for a minimised window",
        "CreateSwapchain with no options",
        "CreateSwapchain without retiring the live one",
        "CreateSwapchain retiring another surface's swapchain",
        "CreateSwapchain retiring a retired swapchain",
        "Destroy a swapchain image's texture",
        "Destroy a swapchain image's view",
        "SetDebugName of a destroyed swapchain's image",
        "GetSwapchainImage of an unknown swapchain",
        "GetSwapchainImage index below range",
        "GetSwapchainImage index above range",
        "DestroySwapchain twice",
        "AcquireImage on a retired swapchain",
        "AcquireImage on a destroyed swapchain",
        "AcquireImage slot below range",
        "AcquireImage slot above range",
        "Present on a retired swapchain",
        "Present image index below range",
        "Present image index above range",
    ];

    [Fact]
    public void Surfaces_SelectionSurfaceHandover()
    {
        WindowSurfaceSource selection = FakeBackendFixture.Window();
        FakeBackendDevice device = _fixture.CreateDevice(compatible: selection);
        ulong firstObject = device.CreateSampler(new SamplerDescription()).Value;

        // The selection surface was made with the device, so its id is below every id created since.
        Assert.Throws<InvalidOperationException>(() => device.DestroySurface(firstObject - 1));
        ulong handedOver = device.CreateSurface(selection).Value;
        ulong other = device.CreateSurface(FakeBackendFixture.Window()).Value;

        Assert.True(handedOver < firstObject, $"{handedOver} should be the selection surface, made before {firstObject}.");
        Assert.True(other > firstObject);
        Assert.Equal(3, device.LiveObjectCount);
        device.DestroySurface(handedOver);
        ulong again = device.CreateSurface(selection).Value;
        Assert.True(again > other, "The selection surface is handed over once; a later surface is a new one.");
    }

    [Fact]
    public void Swapchain_RecreateRetiresOld()
    {
        FakeBackendDevice device = _fixture.CreateDevice();
        (WindowSurfaceSource source, ulong surface, ulong old) = Presentable(device);
        device.GetSwapchainImage(old, 2, out ulong oldTexture, out ulong oldView);

        BackendSwapchain created = device.CreateSwapchain(FakeBackendFixture.Swapchain(surface, source, old)).Value;

        Assert.NotEqual(old, created.Id);
        Assert.Throws<InvalidOperationException>(() => device.AcquireImage(old, 0));
        Assert.Throws<InvalidOperationException>(() => device.Present(old, 0));
        device.GetSwapchainImage(created.Id, 2, out ulong texture, out ulong view);
        Assert.DoesNotContain(texture, new[] { oldTexture, oldView });
        Assert.Equal(SwapchainStatus.Success, device.AcquireImage(created.Id, 0).Status);
        device.DestroySwapchain(old);
        Assert.Throws<InvalidOperationException>(() => device.SetDebugName(BackendObjectKind.Texture, oldTexture, "x"u8));
        Assert.Throws<InvalidOperationException>(() => device.SetDebugName(BackendObjectKind.TextureView, oldView, "x"u8));

        // A failed recreation still retires the swapchain it replaces.
        _fixture.Script.FailNext(FakeBackendOperation.CreateSwapchain, new EngineError(ErrorKind.OutOfMemory, "scripted"));
        Assert.True(device.CreateSwapchain(FakeBackendFixture.Swapchain(surface, source, created.Id)).IsFailure);
        Assert.Throws<InvalidOperationException>(() => device.AcquireImage(created.Id, 0));
        Assert.True(device.CreateSwapchain(FakeBackendFixture.Swapchain(surface, source)).IsSuccess);
    }

    [Fact]
    public void Swapchain_ImagesAreOwnedByTheirSwapchain()
    {
        _fixture.Script.SwapchainInfo = request => FakeBackendScript.DefaultSwapchainInfo(request) with { ImageCount = 2 };
        FakeBackendDevice device = _fixture.CreateDevice();
        (_, ulong surface, ulong swapchain) = Presentable(device);
        var ids = new HashSet<ulong> { surface, swapchain };

        for (int i = 0; i < 2; i++)
        {
            device.GetSwapchainImage(swapchain, i, out ulong texture, out ulong view);
            device.SetDebugName(BackendObjectKind.Texture, texture, "image"u8);
            device.SetDebugName(BackendObjectKind.TextureView, view, "image view"u8);
            Assert.True(ids.Add(texture) && ids.Add(view));
        }

        Assert.Equal(2, device.LiveObjectCount);
        device.DestroySwapchain(swapchain);
        device.DestroySurface(surface);
        Assert.Equal(0, device.LiveObjectCount);
    }

    [Fact]
    public void CreateSwapchain_ReportsTheScriptedInfo()
    {
        FakeBackendDevice device = _fixture.CreateDevice();
        WindowSurfaceSource source = FakeBackendFixture.Window(800, 600);
        ulong surface = device.CreateSurface(source).Value;
        var options = new SwapchainOptions { PresentMode = PresentMode.Mailbox, Format = SwapchainFormat.Unorm };

        BackendSwapchain swapchain = device.CreateSwapchain(new BackendSwapchainRequest(surface, source, options, 0)).Value;

        Assert.Equal(new SwapchainInfo(800, 600, TextureFormat.Bgra8Unorm, PresentMode.Mailbox, 3, true), swapchain.Info);
        Assert.Equal(new FakeBackendCall(FakeBackendOperation.CreateSwapchain, null, swapchain.Id, -1), device.Calls[^1]);
    }

    [Fact]
    public void AcquireImage_CyclesThroughTheImages()
    {
        FakeBackendDevice device = _fixture.CreateDevice();
        (_, _, ulong swapchain) = Presentable(device);
        int[] indices = new int[4];

        for (int i = 0; i < indices.Length; i++)
        {
            BackendAcquire acquire = device.AcquireImage(swapchain, i % 2);
            Assert.Equal((SwapchainStatus.Success, 0), (acquire.Status, acquire.NativeCode));
            Assert.Equal(new BackendPresent(SwapchainStatus.Success, 0), device.Present(swapchain, acquire.ImageIndex));
            indices[i] = acquire.ImageIndex;
        }

        Assert.Equal([0, 1, 2, 0], indices);
        Assert.Equal(new FakeBackendCall(FakeBackendOperation.AcquireImage, null, swapchain, 1), device.Calls[^2]);
    }

    public static TheoryData<FakeBackendOperation, SwapchainStatus> ScriptedStatuses()
    {
        TheoryData<FakeBackendOperation, SwapchainStatus> data = [];
        foreach (FakeBackendOperation operation in new[] { FakeBackendOperation.AcquireImage, FakeBackendOperation.Present })
        {
            foreach (SwapchainStatus status in Enum.GetValues<SwapchainStatus>())
            {
                data.Add(operation, status);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ScriptedStatuses))]
    public void SwapchainStatusNext_ReturnedOnce(FakeBackendOperation operation, SwapchainStatus status)
    {
        FakeBackendDevice device = _fixture.CreateDevice();
        (_, _, ulong swapchain) = Presentable(device);
        _fixture.Script.SwapchainStatusNext(operation, status, 1000 + (int)status);

        (SwapchainStatus Status, int ImageIndex, int Code) first = Call();
        (SwapchainStatus Status, int ImageIndex, int Code) second = Call();

        bool acquired = status is SwapchainStatus.Success or SwapchainStatus.Suboptimal;
        int image = operation is FakeBackendOperation.Present || acquired ? 0 : -1;
        Assert.Equal((status, image, 1000 + (int)status), first);
        Assert.Equal((SwapchainStatus.Success, operation is FakeBackendOperation.Present || !acquired ? 0 : 1, 0), second);

        (SwapchainStatus, int, int) Call()
        {
            if (operation is FakeBackendOperation.Present)
            {
                BackendPresent present = device.Present(swapchain, 0);
                return (present.Status, 0, present.NativeCode);
            }

            BackendAcquire acquire = device.AcquireImage(swapchain, 0);
            return (acquire.Status, acquire.ImageIndex, acquire.NativeCode);
        }
    }

    [Fact]
    public void Dispose_DestroysSurfacesAndSwapchains()
    {
        FakeBackendDevice device = _fixture.CreateDevice(compatible: FakeBackendFixture.Window());
        _ = Presentable(device);

        device.Dispose();

        Assert.Equal(0, device.LiveObjectCount);
        Assert.Throws<InvalidOperationException>(() => device.CreateSurface(FakeBackendFixture.Window()));
    }

    private static (WindowSurfaceSource Source, ulong Surface, ulong Swapchain) Presentable(FakeBackendDevice device)
    {
        WindowSurfaceSource source = FakeBackendFixture.Window();
        ulong surface = device.CreateSurface(source).Value;
        return (source, surface, device.CreateSwapchain(FakeBackendFixture.Swapchain(surface, source)).Value.Id);
    }

    private static Action SwapchainViolation(FakeBackendDevice device, string violation)
    {
        (WindowSurfaceSource source, ulong surface, ulong swapchain) = Presentable(device);
        device.GetSwapchainImage(swapchain, 0, out ulong texture, out ulong view);
        WindowSurfaceSource other = FakeBackendFixture.Window();
        ulong otherSurface = device.CreateSurface(other).Value;
        return violation switch
        {
            "DestroySurface unknown id" => () => device.DestroySurface(999),
            "DestroySurface twice" => Then(() => device.DestroySurface(otherSurface), () => device.DestroySurface(otherSurface)),
            "DestroySurface with a live swapchain" => () => device.DestroySurface(surface),
            "DestroySurface with a retired swapchain" =>
                Then(() => device.DestroySwapchain(Recreate(swapchain)), () => device.DestroySurface(surface)),
            "CreateSurface twice for one window" => () => device.CreateSurface(source),
            "CreateSurface after Dispose" => Then(device.Dispose, () => device.CreateSurface(FakeBackendFixture.Window())),
            "CreateSwapchain on an unknown surface" => () => device.CreateSwapchain(FakeBackendFixture.Swapchain(999, other)),
            "CreateSwapchain on a destroyed surface" =>
                Then(() => device.DestroySurface(otherSurface), () => device.CreateSwapchain(FakeBackendFixture.Swapchain(otherSurface, other))),
            "CreateSwapchain with another window's source" => () => device.CreateSwapchain(FakeBackendFixture.Swapchain(otherSurface, source)),
            "CreateSwapchain for a minimised window" => Minimised(),
            "CreateSwapchain with no options" => () => device.CreateSwapchain(new BackendSwapchainRequest(otherSurface, other, null!, 0)),
            "CreateSwapchain without retiring the live one" => () => device.CreateSwapchain(FakeBackendFixture.Swapchain(surface, source)),
            "CreateSwapchain retiring another surface's swapchain" =>
                () => device.CreateSwapchain(FakeBackendFixture.Swapchain(otherSurface, other, swapchain)),
            "CreateSwapchain retiring a retired swapchain" => Then(() => Recreate(swapchain), () => Recreate(swapchain)),
            "Destroy a swapchain image's texture" => () => device.Destroy(BackendObjectKind.Texture, texture),
            "Destroy a swapchain image's view" => () => device.Destroy(BackendObjectKind.TextureView, view),
            "SetDebugName of a destroyed swapchain's image" =>
                Then(() => device.DestroySwapchain(swapchain), () => device.SetDebugName(BackendObjectKind.Texture, texture, "x"u8)),
            "GetSwapchainImage of an unknown swapchain" => () => device.GetSwapchainImage(999, 0, out _, out _),
            "GetSwapchainImage index below range" => () => device.GetSwapchainImage(swapchain, -1, out _, out _),
            "GetSwapchainImage index above range" => () => device.GetSwapchainImage(swapchain, 3, out _, out _),
            "DestroySwapchain twice" => Then(() => device.DestroySwapchain(swapchain), () => device.DestroySwapchain(swapchain)),
            "AcquireImage on a retired swapchain" => Then(() => Recreate(swapchain), () => device.AcquireImage(swapchain, 0)),
            "AcquireImage on a destroyed swapchain" =>
                Then(() => device.DestroySwapchain(swapchain), () => device.AcquireImage(swapchain, 0)),
            "AcquireImage slot below range" => () => device.AcquireImage(swapchain, -1),
            "AcquireImage slot above range" => () => device.AcquireImage(swapchain, 2),
            "Present on a retired swapchain" => Then(() => Recreate(swapchain), () => device.Present(swapchain, 0)),
            "Present image index below range" => () => device.Present(swapchain, -1),
            "Present image index above range" => () => device.Present(swapchain, 3),
            _ => throw new ArgumentOutOfRangeException(nameof(violation), violation, "Unknown row."),
        };

        ulong Recreate(ulong old) => device.CreateSwapchain(FakeBackendFixture.Swapchain(surface, source, old)).Value.Id;

        Action Minimised()
        {
            WindowSurfaceSource minimised = FakeBackendFixture.Window(minimised: true);
            ulong minimisedSurface = device.CreateSurface(minimised).Value;
            return () => device.CreateSwapchain(FakeBackendFixture.Swapchain(minimisedSurface, minimised));
        }

        static Action Then(Action setup, Action call)
        {
            setup();
            return call;
        }
    }
}
