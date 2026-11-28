using System;
using System.Collections.Generic;
using System.Linq;
using Brutal.VulkanApi;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Vulkan.Swapchains;
using Xunit;
using FrameLoop = BrutalEngine.Graphics.Vulkan.Tests.Swapchains.VulkanSwapchainTests.FrameLoop;
using Gpu = BrutalEngine.Graphics.Vulkan.Tests.Swapchains.VulkanSwapchainTests.Gpu;

namespace BrutalEngine.Graphics.Vulkan.Tests.Swapchains;

// docs/design/graphics-vulkan.md section 4, "Present fences": with swapchain maintenance1 every present carries its
// image's fence, waiting for the image's previous present first, bounded; without it nothing changes.
// The extension is switched off by creating the device without it. Every row runs on every adapter that can present to
// the class's raw surface, with validation on (lavapipe headless; the RTX too when SDL_VIDEO_DRIVER=wayland is set for a
// local run). No row relies on a particular validation message: each asserts its own counts and errors.
[Trait("Category", "Gpu")]
[Collection(nameof(SharedRawSurface))]
public sealed unsafe class PresentFenceTests(VulkanSwapchainTests.SurfaceFixture fixture)
{
    private const int Frames = 2;
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

            VulkanCallGate gate = new();
            VulkanSwapchain.DestroyCounts destroys = new();
            VulkanSwapchain swapchain = gpu.Create(Frames, gate, destroys: destroys);
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
                gpu.WaitIdle();
                swapchain.Dispose();
            }

            Assert.Equal((1, images, images + Frames, images), (destroys.Swapchains, destroys.Views, destroys.Semaphores, destroys.Fences));
            Assert.False(gate.IsLost);
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
    [InlineData((int)VulkanCall.WaitForPresentFence, VkResult.Timeout, true, true)]
    [InlineData((int)VulkanCall.WaitForPresentFence, VkResult.ErrorOutOfDeviceMemory, true, true)]
    [InlineData((int)VulkanCall.WaitForPresentFence, VkResult.ErrorDeviceLost, true, false)]
    [InlineData((int)VulkanCall.ResetFences, VkResult.ErrorOutOfHostMemory, false, false)]
    public void Present_PresentFenceFailure_ReportsWithoutPresenting(int call, VkResult code, bool lost, bool timedOut)
    {
        OnMaintenance1Adapters(gpu =>
        {
            VulkanFaultInjector faults = new();
            VulkanCallGate gate = new() { Faults = faults };
            VulkanSwapchain.DestroyCounts destroys = new();
            VulkanSwapchain swapchain = gpu.Create(Frames, gate, destroys: destroys);
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
            Assert.Equal((1, images, images + Frames, images), (destroys.Swapchains, destroys.Views, destroys.Semaphores, destroys.Fences));
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
            VulkanFaultInjector faults = new();
            VulkanCallGate gate = new() { Faults = faults };
            VulkanSwapchain swapchain = gpu.Create(Frames, gate);
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
        });
    }

    // Without fences: the swapchain makes no present-fence call at all (an armed wait fault stays unconsumed), reports no
    // fence for any image, and destroys cleanly once the caller has waited for idle.
    private static void AssertIdleFallback(Gpu gpu)
    {
        VulkanFaultInjector faults = new();
        faults.FailNth(VulkanCall.WaitForPresentFence, 1, VkResult.ErrorUnknown);
        VulkanCallGate gate = new() { Faults = faults };
        VulkanSwapchain.DestroyCounts destroys = new();
        VulkanSwapchain swapchain = gpu.Create(Frames, gate, destroys: destroys);
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
}
