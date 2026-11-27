using System.Collections.Generic;
using System.Linq;
using BrutalEngine.Graphics.Tests.Backend;
using BrutalEngine.Graphics.Tests.Device;
using BrutalEngine.Platform;
using BrutalEngine.TestUtilities;

namespace BrutalEngine.Graphics.Tests.Swapchains;

/// <summary>Windows, devices and call filters the swapchain tests share.</summary>
internal static class SwapchainTestKit
{
    // The operations of a swapchain's lifetime, in the order the fake recorded them.
    private static readonly FakeBackendOperation[] _lifetime =
    [
        FakeBackendOperation.CreateSurface, FakeBackendOperation.DestroySurface, FakeBackendOperation.CreateSwapchain,
        FakeBackendOperation.DestroySwapchain, FakeBackendOperation.WaitIdle,
    ];

    /// <summary>A window with a surface source and a pixel size.</summary>
    public static FakeWindow Window(int width = 640, int height = 480) =>
        new() { PixelSize = new WindowSize(width, height), VulkanSurface = new FakeVulkanSurfaceSource([], (nint)1) };

    /// <summary>A device whose compatible window is <paramref name="compatible"/>; the fixture disposes it.</summary>
    public static GraphicsDevice NewDevice(DeviceFixture fixture, IWindow compatible, int framesInFlight = 2) =>
        fixture.Create(fixture.Options() with { CompatibleWindow = compatible, FramesInFlight = framesInFlight });

    /// <summary>The surface and swapchain calls (and idle waits) the backend device received, in order.</summary>
    public static List<(FakeBackendOperation Operation, ulong Id)> Lifetime(FakeBackendDevice backend) =>
        [.. backend.Calls.Where(call => _lifetime.Contains(call.Operation)).Select(call => (call.Operation, call.Id))];

    /// <summary>The id of the last call of an operation.</summary>
    public static ulong LastId(FakeBackendDevice backend, FakeBackendOperation operation) =>
        backend.Calls.Last(call => call.Operation == operation).Id;
}
