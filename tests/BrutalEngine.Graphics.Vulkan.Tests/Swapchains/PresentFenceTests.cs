using System;
using System.Collections.Generic;
using System.Linq;
using Brutal.VulkanApi;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Vulkan.Swapchains;
using BrutalEngine.Graphics.Vulkan.Tests.Validation;
using Xunit;
using FrameLoop = BrutalEngine.Graphics.Vulkan.Tests.Swapchains.VulkanSwapchainTests.FrameLoop;
using Gpu = BrutalEngine.Graphics.Vulkan.Tests.Swapchains.VulkanSwapchainTests.Gpu;

namespace BrutalEngine.Graphics.Vulkan.Tests.Swapchains;

// docs/design/graphics-vulkan.md section 4, "Present fences": with swapchain maintenance1 every present carries its
// image's fence and destroying the swapchain waits for them, bounded; without it the caller's idle wait is the fallback.
// The extension is switched off by creating the device without it. Every row runs on every adapter that can present to
// the class's raw surface, with validation on (lavapipe headless; the RTX too when SDL_VIDEO_DRIVER=wayland is set for a
// local run). No row relies on a particular validation message: each asserts its own counts and errors.
[Trait("Category", "Gpu")]
[Collection(nameof(SharedRawSurface))]
public sealed unsafe class PresentFenceTests(VulkanSwapchainTests.SurfaceFixture fixture)
{
    private const int Frames = 2;
    private const string TimedOutLoss = "device is lost after a GPU timeout";
    private static readonly nint _timeout = unchecked((nint)VulkanSwapchainTests.WaitNanoseconds);

    [Fact]
    public void Present_WithMaintenance1_IsValidationClean()
    {
        OnEveryPresentingAdapter(withoutMaintenance1: false, gpu =>
        {
            if (gpu.Core.Extensions.SwapchainMaintenance1 is null)
            {
                // The documented path for a driver without the extension: no fences, the caller's idle wait.
                AssertIdleFallback(gpu);
                return;
            }

            using SwapchainLog log = new();
            VulkanCallGate gate = new();
            VulkanSwapchain.DestroyCounts destroys = new();
            VulkanSwapchain swapchain = gpu.Create(Frames, gate, destroys: destroys, log: log.Category);
            int images = swapchain.ImageCount;
            Assert.True(swapchain.UsesPresentFences);
            nint[] fences = [.. Enumerable.Range(0, images).Select(i => swapchain.GetPresentFence(i).VkHandle)];
            Assert.DoesNotContain((nint)0, fences);
            Assert.Equal(images, fences.Distinct().Count());

            // Past every image twice: an image's second present waits for the fence its first present carried, so a
            // present that didn't carry it (or a fence that was never reset) times out or is reported by the layer.
            using (FrameLoop loop = new(gpu, Frames))
            {
                loop.Run(swapchain, (2 * images) + Frames);

                // No idle wait first: the present fences are what make destroying the swapchain safe.
                swapchain.Dispose();
            }

            Assert.Equal((1, images, images + Frames, images), (destroys.Swapchains, destroys.Views, destroys.Semaphores, destroys.Fences));
            Assert.False(gate.IsLost);
            Assert.Empty(log.Sink.Entries);
        });
    }

    [Fact]
    public void Present_Maintenance1Off_UsesIdleFallback()
    {
        OnEveryPresentingAdapter(withoutMaintenance1: true, gpu =>
        {
            Assert.Null(gpu.Core.Extensions.SwapchainMaintenance1);
            AssertIdleFallback(gpu);
        });
    }

    [Theory]
    [InlineData(VkResult.Timeout)]
    [InlineData(VkResult.ErrorOutOfHostMemory)] // a failed wait proves nothing about the presents either
    public void Destroy_PresentFenceTimeout_MarksTimedOutLossAndKeepsObjects(VkResult code)
    {
        OnMaintenance1Adapters(gpu =>
        {
            using SwapchainLog log = new();
            VulkanFaultInjector faults = new();
            VulkanCallGate gate = new() { Faults = faults };
            VulkanSwapchain.DestroyCounts destroys = new();
            VulkanSwapchain swapchain = gpu.Create(Frames, gate, destroys: destroys, log: log.Category);
            RunFrames(gpu, swapchain);
            gpu.WaitIdle();
            faults.FailNth(VulkanCall.WaitForPresentFence, 1, code);

            swapchain.Dispose();

            Assert.False(faults.TryTake(VulkanCall.WaitForPresentFence, out _), "the fault didn't fire");
            Assert.True(gate.IsLost);
            Assert.True(gate.LostTimedOut);
            Assert.True(swapchain.IsDisposed);
            Assert.False(swapchain.Handle.IsNull());
            Assert.Equal((0, 0, 0, 0), (destroys.Swapchains, destroys.Views, destroys.Semaphores, destroys.Fences));
            (LogLevel Level, string Message) entry = Assert.Single(log.Sink.Entries);
            Assert.Equal(LogLevel.Error, entry.Level);
            Assert.Contains("after a GPU timeout", entry.Message, StringComparison.Ordinal);

            // Later calls report the timeout loss without calling the driver; a second Dispose does nothing.
            faults.FailNth(VulkanCall.CreateSwapchain, 1, VkResult.ErrorUnknown);
            Result<VulkanSwapchain> created = VulkanSwapchain.Create(gpu.Core, gpu.Raw.Surface, gpu.Plan, null, Frames, gate, _timeout, destroys, log.Category);
            Assert.Equal(new EngineError(ErrorKind.Timeout, TimedOutLoss, (int)VkResult.Timeout), created.Error);
            Assert.True(faults.TryTake(VulkanCall.CreateSwapchain, out _), "create reached the driver");
            swapchain.Dispose();
            Assert.Single(log.Sink.Entries);
            Assert.Equal((0, 0, 0, 0), (destroys.Swapchains, destroys.Views, destroys.Semaphores, destroys.Fences));

            DestroyKept(gpu, swapchain);
        });
    }

    [Fact]
    public void Destroy_PresentFenceDeviceLost_MarksLoss()
    {
        OnMaintenance1Adapters(gpu =>
        {
            using SwapchainLog log = new();
            VulkanFaultInjector faults = new();
            VulkanCallGate gate = new() { Faults = faults };
            VulkanSwapchain.DestroyCounts destroys = new();
            VulkanSwapchain swapchain = gpu.Create(Frames, gate, destroys: destroys, log: log.Category);
            int images = swapchain.ImageCount;
            RunFrames(gpu, swapchain);
            gpu.WaitIdle();
            faults.FailNth(VulkanCall.WaitForPresentFence, 1, VkResult.ErrorDeviceLost);

            swapchain.Dispose();

            // A reported loss: destroying stays valid, so everything is destroyed (the layer reports any leak).
            Assert.False(faults.TryTake(VulkanCall.WaitForPresentFence, out _), "the fault didn't fire");
            Assert.True(gate.IsLost);
            Assert.False(gate.LostTimedOut);
            Assert.True(swapchain.Handle.IsNull());
            Assert.Equal((1, images, images + Frames, images), (destroys.Swapchains, destroys.Views, destroys.Semaphores, destroys.Fences));
            Assert.Empty(log.Sink.Entries);
            Result<VulkanSwapchain> created = VulkanSwapchain.Create(gpu.Core, gpu.Raw.Surface, gpu.Plan, null, Frames, gate, _timeout, log: log.Category);
            Assert.Equal(new EngineError(ErrorKind.Unavailable, "device is lost", (int)VkResult.ErrorDeviceLost), created.Error);
        });
    }

    [Theory]
    [InlineData((int)VulkanCall.WaitForPresentFence, VkResult.Timeout, true, true)]
    [InlineData((int)VulkanCall.WaitForPresentFence, VkResult.ErrorOutOfDeviceMemory, true, true)]
    [InlineData((int)VulkanCall.WaitForPresentFence, VkResult.ErrorDeviceLost, true, false)]
    [InlineData((int)VulkanCall.ResetFences, VkResult.ErrorOutOfHostMemory, false, false)]
    public void Present_PresentFenceFailure_ReportsWithoutPresenting(int call, VkResult code, bool lost, bool timedOut)
    {
        OnMaintenance1Adapters(gpu =>
        {
            using SwapchainLog log = new();
            VulkanFaultInjector faults = new();
            VulkanCallGate gate = new() { Faults = faults };
            VulkanSwapchain.DestroyCounts destroys = new();
            VulkanSwapchain swapchain = gpu.Create(Frames, gate, destroys: destroys, log: log.Category);
            int images = swapchain.ImageCount;
            using FrameLoop loop = new(gpu, Frames);
            int image = loop.AcquireAndSubmit(swapchain, 0);
            faults.FailNth((VulkanCall)call, 1, code);
            faults.FailNth(VulkanCall.QueuePresent, 1, VkResult.ErrorUnknown);

            Assert.Equal(new BackendPresent(SwapchainStatus.DeviceLost, (int)code), swapchain.Present(gpu.Core.Queue, image));

            Assert.False(faults.TryTake((VulkanCall)call, out _), "the fault didn't fire");
            Assert.True(faults.TryTake(VulkanCall.QueuePresent, out _), "the image was presented");
            Assert.Equal((lost, timedOut), (gate.IsLost, gate.LostTimedOut));
            gpu.WaitIdle();
            swapchain.Dispose();
            if (timedOut)
            {
                Assert.Equal((0, 0, 0, 0), (destroys.Swapchains, destroys.Views, destroys.Semaphores, destroys.Fences));
                Assert.Equal(LogLevel.Error, Assert.Single(log.Sink.Entries).Level);
                DestroyKept(gpu, swapchain);
            }
            else
            {
                Assert.Equal((1, images, images + Frames, images), (destroys.Swapchains, destroys.Views, destroys.Semaphores, destroys.Fences));
                Assert.Empty(log.Sink.Entries);
            }
        });
    }

    [Fact]
    public void Present_InjectedPresentFailure_LeavesFenceUnwaited()
    {
        OnMaintenance1Adapters(gpu =>
        {
            using SwapchainLog log = new();
            VulkanFaultInjector faults = new();
            VulkanCallGate gate = new() { Faults = faults };
            VulkanSwapchain.DestroyCounts destroys = new();
            VulkanSwapchain swapchain = gpu.Create(Frames, gate, destroys: destroys, log: log.Category);
            int images = swapchain.ImageCount;
            using FrameLoop loop = new(gpu, Frames);
            int image = loop.AcquireAndSubmit(swapchain, 0);
            faults.FailNth(VulkanCall.QueuePresent, 1, VkResult.ErrorOutOfDateKHR);
            Assert.Equal(new BackendPresent(SwapchainStatus.OutOfDate, (int)VkResult.ErrorOutOfDateKHR), swapchain.Present(gpu.Core.Queue, image));
            gpu.WaitIdle();

            // The injected present replaced the call, so the reset fence was never handed to the driver and will never
            // signal: destroying waits only for fences a present was enqueued with, so this is no timeout.
            swapchain.Dispose();

            Assert.False(gate.IsLost);
            Assert.Equal((1, images, images + Frames, images), (destroys.Swapchains, destroys.Views, destroys.Semaphores, destroys.Fences));
            Assert.Empty(log.Sink.Entries);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_PresentFenceCreationFails_DestroysPartialState(bool lastImage)
    {
        OnMaintenance1Adapters(gpu =>
        {
            int images;
            using (VulkanSwapchain probe = gpu.Create(Frames))
            {
                images = probe.ImageCount;
            }

            int occurrence = lastImage ? images : 1;
            VulkanFaultInjector faults = new();
            faults.FailNth(VulkanCall.CreateFence, occurrence, VkResult.ErrorOutOfDeviceMemory);
            VulkanSwapchain.DestroyCounts destroys = new();

            Result<VulkanSwapchain> result = VulkanSwapchain.Create(gpu.Core, gpu.Raw.Surface, gpu.Plan, null, Frames, new VulkanCallGate { Faults = faults }, _timeout, destroys);

            Assert.Equal(VkCheck.Error(VkResult.ErrorOutOfDeviceMemory, VulkanCall.CreateFence), result.Error);
            Assert.False(faults.TryTake(VulkanCall.CreateFence, out _), "the fault didn't fire");
            Assert.Equal((1, images, images + Frames, occurrence - 1), (destroys.Swapchains, destroys.Views, destroys.Semaphores, destroys.Fences));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Present_DoesNotAllocate(bool withoutMaintenance1)
    {
        OnEveryPresentingAdapter(withoutMaintenance1, gpu =>
        {
            bool fenced = gpu.Core.Extensions.SwapchainMaintenance1 is not null;
            Assert.True(!withoutMaintenance1 || !fenced);
            using SwapchainLog log = new();
            VulkanFaultInjector faults = new();
            VulkanCallGate gate = new() { Faults = faults };
            VulkanSwapchain swapchain = gpu.Create(Frames, gate, log: log.Category);
            Assert.Equal(fenced, swapchain.UsesPresentFences);
            using FrameLoop loop = new(gpu, Frames);
            loop.Run(swapchain, swapchain.ImageCount + Frames);
            VkQueue queue = gpu.Core.Queue;
            long allocated = 0;
            int checksum = 0;

            // The success path over every image, each presented again, so each fence is waited for and reset.
            int frames = 3 * swapchain.ImageCount;
            for (int frame = 0; frame < frames; frame++)
            {
                int slot = frame % Frames;
                loop.WaitSlot(slot);
                int image = loop.AcquireAndSubmit(swapchain, slot);
                long before = GC.GetAllocatedBytesForCurrentThread();
                BackendPresent presented = swapchain.Present(queue, image);
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.Equal(SwapchainStatus.Success, presented.Status);
                checksum += (int)presented.Status + image + 1;
            }

            // The failure paths: an injected present result, then a fence wait that times out (or, without fences, a
            // reported loss from the present), then a present on the lost gate.
            loop.WaitSlot(0);
            int failed = loop.AcquireAndSubmit(swapchain, 0);
            faults.FailNth(VulkanCall.QueuePresent, 1, VkResult.ErrorOutOfDateKHR);
            long failureBefore = GC.GetAllocatedBytesForCurrentThread();
            checksum += (int)swapchain.Present(queue, failed).Status;
            allocated += GC.GetAllocatedBytesForCurrentThread() - failureBefore;
            loop.WaitSlot(1);
            failed = loop.AcquireAndSubmit(swapchain, 1);
            faults.FailNth(fenced ? VulkanCall.WaitForPresentFence : VulkanCall.QueuePresent, 1, fenced ? VkResult.Timeout : VkResult.ErrorDeviceLost);
            failureBefore = GC.GetAllocatedBytesForCurrentThread();
            checksum += (int)swapchain.Present(queue, failed).Status;
            checksum += (int)swapchain.Present(queue, failed).Status;
            allocated += GC.GetAllocatedBytesForCurrentThread() - failureBefore;

            Assert.Equal(0, allocated);
            Assert.Equal((true, fenced), (gate.IsLost, gate.LostTimedOut));
            Assert.True(checksum > 0);
            gpu.WaitIdle();
            swapchain.Dispose();
            if (fenced)
            {
                DestroyKept(gpu, swapchain);
            }
        });
    }

    // Without fences: the swapchain makes no present-fence call at all (an armed wait fault stays unconsumed), reports no
    // fence for any image, and destroys cleanly once the caller has waited for idle.
    private static void AssertIdleFallback(Gpu gpu)
    {
        using SwapchainLog log = new();
        VulkanFaultInjector faults = new();
        faults.FailNth(VulkanCall.WaitForPresentFence, 1, VkResult.ErrorUnknown);
        VulkanCallGate gate = new() { Faults = faults };
        VulkanSwapchain.DestroyCounts destroys = new();
        VulkanSwapchain swapchain = gpu.Create(Frames, gate, destroys: destroys, log: log.Category);
        int images = swapchain.ImageCount;
        Assert.False(swapchain.UsesPresentFences);
        Assert.All(Enumerable.Range(0, images), i => Assert.True(swapchain.GetPresentFence(i).IsNull()));
        Assert.Throws<IndexOutOfRangeException>(() => swapchain.GetPresentFence(images));
        using (FrameLoop loop = new(gpu, Frames))
        {
            loop.Run(swapchain, (2 * images) + Frames);
            gpu.WaitIdle();
            swapchain.Dispose();
        }

        Assert.True(faults.TryTake(VulkanCall.WaitForPresentFence, out _), "a present fence was waited for");
        Assert.Equal((1, images, images + Frames, 0), (destroys.Swapchains, destroys.Views, destroys.Semaphores, destroys.Fences));
        Assert.False(gate.IsLost);
        Assert.Empty(log.Sink.Entries);
    }

    private static void RunFrames(Gpu gpu, VulkanSwapchain swapchain)
    {
        using FrameLoop loop = new(gpu, Frames);
        loop.Run(swapchain, swapchain.ImageCount + Frames);
    }

    // The test's own cleanup of what a swapchain kept after a timeout loss, straight through the driver once the queue is
    // idle, so the device's destruction reports no leak. Had the swapchain destroyed any of them after all, the layer would
    // report the second destroy of a handle that no longer exists, so this also proves they were kept.
    private static void DestroyKept(Gpu gpu, VulkanSwapchain swapchain)
    {
        gpu.WaitIdle();
        Device device = gpu.Core.Device;
        for (int i = 0; i < swapchain.ImageCount; i++)
        {
            device.PInvoke.vkDestroyFence.Invoke(device.Handle, swapchain.GetPresentFence(i), null);
            device.PInvoke.vkDestroySemaphore.Invoke(device.Handle, swapchain.GetRenderFinished(i), null);
            device.PInvoke.vkDestroyImageView.Invoke(device.Handle, swapchain.GetView(i), null);
        }

        for (int slot = 0; slot < swapchain.FramesInFlight; slot++)
        {
            device.PInvoke.vkDestroySemaphore.Invoke(device.Handle, swapchain.GetAcquireSemaphore(slot), null);
        }

        device.PInvoke.vkDestroySwapchainKHR.Invoke(device.Handle, swapchain.Handle, null);
    }

    // Rows that need the fence path run on the adapters whose device enables the extension, and fail if there is none.
    private void OnMaintenance1Adapters(Action<Gpu> body)
    {
        List<string> ran = [];
        OnEveryPresentingAdapter(withoutMaintenance1: false, gpu =>
        {
            if (gpu.Core.Extensions.SwapchainMaintenance1 is not null)
            {
                ran.Add(gpu.Adapter.Candidate.Name);
                body(gpu);
            }
        });
        Assert.True(ran.Count > 0, "no presenting adapter enables swapchain maintenance1");
    }

    private void OnEveryPresentingAdapter(bool withoutMaintenance1, Action<Gpu> body) =>
        VulkanSwapchainTests.OnEveryPresentingAdapter(fixture, withoutMaintenance1, null, body);

    // The swapchain's own log, apart from the validation messages, so a row counts exactly what the swapchain logged.
    private sealed class SwapchainLog : IDisposable
    {
        private readonly Logger _logger;

        public SwapchainLog()
        {
            Sink = new ValidationLog();
            (_logger, Category) = ValidationLog.Create(Sink);
        }

        public ValidationLog Sink { get; }

        public LogCategory Category { get; }

        public void Dispose() => _logger.Dispose();
    }
}
