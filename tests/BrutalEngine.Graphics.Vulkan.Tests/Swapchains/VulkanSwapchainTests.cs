using System;
using System.Collections.Generic;
using System.Linq;
using Brutal.VulkanApi;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Vulkan.Devices;
using BrutalEngine.Graphics.Vulkan.Swapchains;
using BrutalEngine.Graphics.Vulkan.Tests.Devices;
using BrutalEngine.Graphics.Vulkan.Tests.Validation;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Swapchains;

// docs/design/graphics-vulkan.md section 4: the swapchain component on a raw surface, on every adapter that can present
// to it (lavapipe headless; the RTX too when SDL_VIDEO_DRIVER=wayland is set for a local run), with validation on.
[Trait("Category", "Gpu")]
[Collection(nameof(SharedRawSurface))]
public sealed unsafe class VulkanSwapchainTests(VulkanSwapchainTests.SurfaceFixture fixture)
{
    // Every wait in the harness, and the acquire timeout (GpuTimeout's form: nanoseconds as BRUTAL's nint).
    internal const long WaitNanoseconds = 5_000_000_000;
    private const string ZeroExtent = "surface extent is zero";
    private static readonly nint _timeout = unchecked((nint)WaitNanoseconds);

    // The failing call; which of its calls fails, then how many views and semaphores were created before it, each as
    // constant + per image (N) + per frame slot (F); the injected code, the kind and message section 13 gives it.
    // Creation order: the swapchain, the image count and fill, then per image a view and its render-finished semaphore,
    // then per slot an acquire semaphore.
    public static TheoryData<int, int[], int, int[], int[], int> Steps => new()
    {
        { (int)VulkanCall.CreateSwapchain, [1, 0, 0], 0, [0, 0, 0], [0, 0, 0], (int)VkResult.ErrorOutOfDeviceMemory },
        { (int)VulkanCall.GetSwapchainImages, [1, 0, 0], 1, [0, 0, 0], [0, 0, 0], (int)VkResult.ErrorOutOfHostMemory },
        { (int)VulkanCall.GetSwapchainImages, [2, 0, 0], 1, [0, 0, 0], [0, 0, 0], (int)VkResult.Incomplete },
        { (int)VulkanCall.CreateImageView, [1, 0, 0], 1, [0, 0, 0], [0, 0, 0], (int)VkResult.ErrorOutOfDeviceMemory },
        { (int)VulkanCall.CreateImageView, [0, 1, 0], 1, [-1, 1, 0], [-1, 1, 0], (int)VkResult.ErrorOutOfHostMemory },
        { (int)VulkanCall.CreateSemaphore, [1, 0, 0], 1, [1, 0, 0], [0, 0, 0], (int)VkResult.ErrorOutOfDeviceMemory },
        { (int)VulkanCall.CreateSemaphore, [0, 1, 0], 1, [0, 1, 0], [-1, 1, 0], (int)VkResult.ErrorOutOfHostMemory },
        { (int)VulkanCall.CreateSemaphore, [1, 1, 0], 1, [0, 1, 0], [0, 1, 0], (int)VkResult.ErrorOutOfDeviceMemory },
        { (int)VulkanCall.CreateSemaphore, [0, 1, 1], 1, [0, 1, 0], [-1, 1, 1], (int)VkResult.ErrorOutOfHostMemory },
    };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Create_RealSurface_ReportsImagesAndDisposesCleanly(int framesInFlight)
    {
        OnEveryPresentingAdapter(gpu =>
        {
            VulkanSwapchain.DestroyCounts destroys = new();
            VulkanSwapchain swapchain = gpu.Create(framesInFlight, destroys: destroys);
            int count = swapchain.ImageCount;
            VkImage[] driverImages = gpu.DriverImages(swapchain);

            Assert.Equal(gpu.Plan, swapchain.Plan);
            Assert.False(swapchain.Handle.IsNull());
            Assert.True(count >= gpu.Plan.ImageCount, $"{count} images for a minimum of {gpu.Plan.ImageCount}");
            Assert.Equal(framesInFlight, swapchain.FramesInFlight);
            Assert.Equal(driverImages.Select(i => i.VkHandle), Enumerable.Range(0, count).Select(i => swapchain.GetImage(i).VkHandle));
            AssertDistinctLive(Enumerable.Range(0, count).Select(i => swapchain.GetView(i).VkHandle), count);
            AssertDistinctLive(
                Enumerable.Range(0, count).Select(i => swapchain.GetRenderFinished(i).VkHandle)
                    .Concat(Enumerable.Range(0, framesInFlight).Select(s => swapchain.GetAcquireSemaphore(s).VkHandle)),
                count + framesInFlight);

            swapchain.Dispose();
            swapchain.Dispose();

            Assert.True(swapchain.IsDisposed);
            Assert.True(swapchain.Handle.IsNull());
            Assert.Equal((1, count, count + framesInFlight), (destroys.Swapchains, destroys.Views, destroys.Semaphores));
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AcquirePresent_RealSurface_Succeeds(int framesInFlight)
    {
        OnEveryPresentingAdapter(gpu =>
        {
            VulkanSwapchain swapchain = gpu.Create(framesInFlight);
            using FrameLoop loop = new(gpu, framesInFlight);

            // Past every image twice, so each render-finished semaphore and acquire semaphore is reused, which validation
            // reports if a present didn't wait on the one it was given.
            loop.Run(swapchain, (2 * swapchain.ImageCount) + framesInFlight);
        });
    }

    [Fact]
    public void Recreate_WithOldSwapchain_Succeeds()
    {
        // The proof that the recreation passed the old swapchain, on every driver: the old one is retired, so the layer
        // reports an acquire from it (lavapipe accepts a second swapchain on the surface either way, and still acquires).
        OnEveryPresentingAdapter(Retired, gpu =>
        {
            VulkanSwapchain.DestroyCounts oldDestroys = new();
            VulkanSwapchain old = gpu.Create(2, destroys: oldDestroys);
            using FrameLoop loop = new(gpu, 2);
            loop.Run(old, 2);
            gpu.WaitIdle();

            VulkanSwapchain replacement = gpu.Create(2, old: old);
            if (old.Acquire(0, 0).Status is SwapchainStatus.Success or SwapchainStatus.Suboptimal)
            {
                loop.Consume(old, 0);
            }

            gpu.WaitIdle();

            // The front end destroys the retired swapchain after the device is idle, whether creation succeeded or not.
            Assert.False(old.IsDisposed);
            old.Dispose();
            Assert.Equal((1, old.ImageCount), (oldDestroys.Swapchains, oldDestroys.Views));
            Assert.NotEqual(old.GetImage(0).VkHandle, replacement.GetImage(0).VkHandle);
            loop.Run(replacement, replacement.ImageCount + 2);
        });
    }

    [Fact]
    public void Acquire_NoImageFree_ReturnsOutOfDateWithoutIndex()
    {
        OnEveryPresentingAdapter(gpu =>
        {
            VulkanSwapchain swapchain = gpu.Create(1);
            using FrameLoop loop = new(gpu, 1);

            // Hold every image the driver hands out (none is presented), then ask once more without waiting: the driver
            // itself answers NOT_READY (or TIMEOUT), a status with no image.
            int held = 0;
            BackendAcquire acquired = swapchain.Acquire(0, 0);
            while (acquired.Status is SwapchainStatus.Success or SwapchainStatus.Suboptimal)
            {
                Assert.True(++held <= swapchain.ImageCount, $"{held} images held of {swapchain.ImageCount}");
                loop.Consume(swapchain, 0);
                acquired = swapchain.Acquire(0, 0);
            }

            Assert.Equal(SwapchainStatus.OutOfDate, acquired.Status);
            Assert.Equal(-1, acquired.ImageIndex);
            Assert.Contains((VkResult)acquired.NativeCode, (VkResult[])[VkResult.NotReady, VkResult.Timeout]);
        });
    }

    [Theory]
    [InlineData(VkResult.ErrorOutOfDateKHR, SwapchainStatus.OutOfDate)]
    [InlineData(VkResult.ErrorSurfaceLostKHR, SwapchainStatus.SurfaceLost)]
    [InlineData(VkResult.Timeout, SwapchainStatus.OutOfDate)]
    [InlineData(VkResult.NotReady, SwapchainStatus.OutOfDate)]
    [InlineData(VkResult.ErrorOutOfHostMemory, SwapchainStatus.DeviceLost)]
    [InlineData(VkResult.ErrorDeviceLost, SwapchainStatus.DeviceLost)]
    public void Acquire_InjectedResult_MapsStatus(VkResult code, SwapchainStatus status)
    {
        OnEveryPresentingAdapter(gpu =>
        {
            VulkanFaultInjector faults = new();
            VulkanCallGate gate = new() { Faults = faults };
            VulkanSwapchain swapchain = gpu.Create(2, gate);
            faults.FailNth(VulkanCall.AcquireNextImage, 1, code);

            Assert.Equal(new BackendAcquire(status, -1, (int)code), swapchain.Acquire(0, _timeout));

            bool lost = code == VkResult.ErrorDeviceLost;
            Assert.Equal(lost, gate.IsLost);
            if (lost)
            {
                AssertLostMakesNoDriverCall(gpu, swapchain, faults, gate);
            }
            else
            {
                // The injected result replaced the call, so slot 0's semaphore is untouched and a real frame works.
                using FrameLoop loop = new(gpu, 2);
                loop.Run(swapchain, 1);
            }
        });
    }

    [Theory]
    [InlineData(VkResult.ErrorOutOfDateKHR, SwapchainStatus.OutOfDate)]
    [InlineData(VkResult.SuboptimalKHR, SwapchainStatus.Suboptimal)]
    [InlineData(VkResult.ErrorSurfaceLostKHR, SwapchainStatus.SurfaceLost)]
    [InlineData(VkResult.Timeout, SwapchainStatus.DeviceLost)] // a present can't time out
    [InlineData(VkResult.ErrorDeviceLost, SwapchainStatus.DeviceLost)]
    public void Present_InjectedResult_MapsStatus(VkResult code, SwapchainStatus status)
    {
        OnEveryPresentingAdapter(gpu =>
        {
            VulkanFaultInjector faults = new();
            VulkanCallGate gate = new() { Faults = faults };
            VulkanSwapchain swapchain = gpu.Create(2, gate);
            using FrameLoop loop = new(gpu, 2);
            int image = loop.AcquireAndSubmit(swapchain, 0);
            faults.FailNth(VulkanCall.QueuePresent, 1, code);

            Assert.Equal(new BackendPresent(status, (int)code), swapchain.Present(gpu.Core.Queue, image));

            bool lost = code == VkResult.ErrorDeviceLost;
            Assert.Equal(lost, gate.IsLost);
            if (lost)
            {
                AssertLostMakesNoDriverCall(gpu, swapchain, faults, gate);
            }
        });
    }

    [Theory]
    [MemberData(nameof(Steps))]
    public void Create_InjectedFailureAtEachStep_DestroysPartialState(int call, int[] occurrence, int swapchains, int[] views, int[] semaphores, int code)
    {
        const int Frames = 3;
        OnEveryPresentingAdapter(gpu =>
        {
            int images;
            using (VulkanSwapchain probe = gpu.Create(Frames))
            {
                images = probe.ImageCount;
            }

            VulkanFaultInjector faults = new();
            faults.FailNth((VulkanCall)call, Of(occurrence), (VkResult)code);
            VulkanSwapchain.DestroyCounts destroys = new();

            Result<VulkanSwapchain> result = VulkanSwapchain.Create(gpu.Core, gpu.Raw.Surface, gpu.Plan, null, Frames, new VulkanCallGate { Faults = faults }, _timeout, destroys);

            Assert.Equal(VkCheck.Error((VkResult)code, (VulkanCall)call), result.Error);
            Assert.False(faults.TryTake((VulkanCall)call, out _), "the fault didn't fire");
            Assert.Equal((swapchains, Of(views), Of(semaphores)), (destroys.Swapchains, destroys.Views, destroys.Semaphores));

            int Of(int[] terms) => terms[0] + (terms[1] * images) + (terms[2] * Frames);
        });
    }

    [Theory]
    [InlineData(0, 64)]
    [InlineData(64, 0)]
    [InlineData(-1, 64)]
    [InlineData(64, -1)]
    public void Create_ZeroExtent_IsCancelled(int width, int height)
    {
        OnEveryPresentingAdapter(gpu =>
        {
            VulkanFaultInjector faults = new();
            faults.FailNth(VulkanCall.CreateSwapchain, 1, VkResult.ErrorUnknown);
            VulkanSwapchain.DestroyCounts destroys = new();

            Result<VulkanSwapchain> result = VulkanSwapchain.Create(
                gpu.Core, gpu.Raw.Surface, gpu.Plan with { Width = width, Height = height }, null, 2, new VulkanCallGate { Faults = faults }, _timeout, destroys);

            Assert.Equal(new EngineError(ErrorKind.Cancelled, ZeroExtent), result.Error);
            Assert.True(faults.TryTake(VulkanCall.CreateSwapchain, out _), "a driver call was made");
            Assert.Equal((0, 0, 0), (destroys.Swapchains, destroys.Views, destroys.Semaphores));
        });
    }

    [Fact]
    public void AcquirePresent_DoNotAllocate()
    {
        OnEveryPresentingAdapter(gpu =>
        {
            VulkanFaultInjector faults = new();
            VulkanCallGate gate = new() { Faults = faults };
            VulkanSwapchain swapchain = gpu.Create(2, gate);
            using FrameLoop loop = new(gpu, 2);
            loop.Run(swapchain, swapchain.ImageCount + 2);
            VkQueue queue = gpu.Core.Queue;
            long allocated = 0;
            int checksum = 0;

            // The success paths, every image and both slots: only the component's calls are inside the windows.
            int frames = 3 * swapchain.ImageCount;
            for (int frame = 0; frame < frames; frame++)
            {
                int slot = frame % 2;
                loop.WaitSlot(slot);
                long before = GC.GetAllocatedBytesForCurrentThread();
                BackendAcquire acquired = swapchain.Acquire(slot, _timeout);
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.Equal(SwapchainStatus.Success, acquired.Status);
                loop.Submit(swapchain, slot, acquired.ImageIndex);
                before = GC.GetAllocatedBytesForCurrentThread();
                BackendPresent presented = swapchain.Present(queue, acquired.ImageIndex);
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                checksum += (int)presented.Status + acquired.ImageIndex;
            }

            // The failure paths: an injected acquire and present status, a loss, then calls on the lost gate.
            loop.WaitSlot(0);
            int image = loop.AcquireAndSubmit(swapchain, 0);
            faults.FailNth(VulkanCall.QueuePresent, 1, VkResult.ErrorOutOfDateKHR);
            faults.FailNth(VulkanCall.AcquireNextImage, 1, VkResult.ErrorOutOfDateKHR);
            long failureBefore = GC.GetAllocatedBytesForCurrentThread();
            checksum += (int)swapchain.Present(queue, image).Status;
            checksum += (int)swapchain.Acquire(1, _timeout).Status;
            allocated += GC.GetAllocatedBytesForCurrentThread() - failureBefore;
            faults.FailNth(VulkanCall.AcquireNextImage, 1, VkResult.ErrorDeviceLost);
            failureBefore = GC.GetAllocatedBytesForCurrentThread();
            checksum += (int)swapchain.Acquire(1, _timeout).Status;
            checksum += (int)swapchain.Acquire(0, _timeout).Status;
            checksum += (int)swapchain.Present(queue, image).Status;
            allocated += GC.GetAllocatedBytesForCurrentThread() - failureBefore;

            // The getters, over every image and slot, on the lost swapchain (it stays readable).
            long gettersBefore = GC.GetAllocatedBytesForCurrentThread();
            nint handles = swapchain.Handle.VkHandle + swapchain.Plan.Width + swapchain.FramesInFlight + (swapchain.IsDisposed ? 1 : 0) + (swapchain.UsesPresentFences ? 1 : 0);
            for (int i = 0; i < swapchain.ImageCount; i++)
            {
                handles += swapchain.GetImage(i).VkHandle + swapchain.GetView(i).VkHandle + swapchain.GetRenderFinished(i).VkHandle + swapchain.GetPresentFence(i).VkHandle;
            }

            for (int slot = 0; slot < swapchain.FramesInFlight; slot++)
            {
                handles += swapchain.GetAcquireSemaphore(slot).VkHandle;
            }

            allocated += GC.GetAllocatedBytesForCurrentThread() - gettersBefore;

            Assert.Equal(0, allocated);
            Assert.True(gate.IsLost);
            Assert.True(checksum > 0 && handles != 0);
        });
    }

    [Fact]
    public void Misuse_ThrowsBeforeAnyDriverCall()
    {
        OnEveryPresentingAdapter(gpu =>
        {
            VulkanFaultInjector faults = new();
            faults.FailNth(VulkanCall.CreateSwapchain, 1, VkResult.ErrorUnknown);
            VulkanCallGate gate = new() { Faults = faults };
            VkSurfaceKHR surface = gpu.Raw.Surface;
            VulkanSwapchain disposed = gpu.Create(1);
            disposed.Dispose();
            using VulkanDeviceCore gone = gpu.Raw.CreateDevice(gpu.Adapter, new VulkanCallGate());
            gone.Dispose();

            Assert.Throws<ArgumentNullException>("core", () => VulkanSwapchain.Create(null!, surface, gpu.Plan, null, 2, gate, _timeout));
            Assert.Throws<ArgumentNullException>("gate", () => VulkanSwapchain.Create(gpu.Core, surface, gpu.Plan, null, 2, null!, _timeout));
            Assert.Throws<ArgumentException>("surface", () => VulkanSwapchain.Create(gpu.Core, default, gpu.Plan, null, 2, gate, _timeout));
            Assert.Throws<ArgumentOutOfRangeException>("framesInFlight", () => VulkanSwapchain.Create(gpu.Core, surface, gpu.Plan, null, 0, gate, _timeout));
            Assert.Throws<ArgumentOutOfRangeException>("framesInFlight", () => VulkanSwapchain.Create(gpu.Core, surface, gpu.Plan, null, 4, gate, _timeout));
            Assert.Throws<ObjectDisposedException>(() => VulkanSwapchain.Create(gpu.Core, surface, gpu.Plan, disposed, 2, gate, _timeout));
            Assert.Throws<ObjectDisposedException>(() => VulkanSwapchain.Create(gone, surface, gpu.Plan, null, 2, gate, _timeout));
            Assert.True(faults.TryTake(VulkanCall.CreateSwapchain, out _), "a driver call was made");

            VulkanSwapchain swapchain = gpu.Create(2);
            Assert.Throws<ArgumentOutOfRangeException>("slot", () => swapchain.Acquire(-1, _timeout));
            Assert.Throws<ArgumentOutOfRangeException>("slot", () => swapchain.Acquire(2, _timeout));
            Assert.Throws<ArgumentOutOfRangeException>("imageIndex", () => swapchain.Present(gpu.Core.Queue, -1));
            Assert.Throws<ArgumentOutOfRangeException>("imageIndex", () => swapchain.Present(gpu.Core.Queue, swapchain.ImageCount));
            Assert.Throws<IndexOutOfRangeException>(() => swapchain.GetImage(swapchain.ImageCount));
            Assert.Throws<IndexOutOfRangeException>(() => swapchain.GetView(-1));
            Assert.Throws<IndexOutOfRangeException>(() => swapchain.GetAcquireSemaphore(2));
            Assert.Throws<ObjectDisposedException>(() => disposed.Acquire(0, _timeout));
            Assert.Throws<ObjectDisposedException>(() => disposed.Present(gpu.Core.Queue, 0));
        });
    }

    // With the gate lost, create, acquire and present return at once: faults armed on each call stay unconsumed.
    private static void AssertLostMakesNoDriverCall(Gpu gpu, VulkanSwapchain swapchain, VulkanFaultInjector faults, VulkanCallGate gate)
    {
        faults.FailNth(VulkanCall.AcquireNextImage, 1, VkResult.ErrorUnknown);
        faults.FailNth(VulkanCall.QueuePresent, 1, VkResult.ErrorUnknown);
        faults.FailNth(VulkanCall.CreateSwapchain, 1, VkResult.ErrorUnknown);
        VulkanSwapchain.DestroyCounts destroys = new();

        Assert.Equal(new BackendAcquire(SwapchainStatus.DeviceLost, -1, (int)VkResult.ErrorDeviceLost), swapchain.Acquire(1, _timeout));
        Assert.Equal(new BackendPresent(SwapchainStatus.DeviceLost, (int)VkResult.ErrorDeviceLost), swapchain.Present(gpu.Core.Queue, 0));
        Result<VulkanSwapchain> created = VulkanSwapchain.Create(gpu.Core, gpu.Raw.Surface, gpu.Plan, swapchain, 2, gate, _timeout, destroys);

        Assert.Equal(new EngineError(ErrorKind.Unavailable, "device is lost", (int)VkResult.ErrorDeviceLost), created.Error);
        Assert.True(faults.TryTake(VulkanCall.AcquireNextImage, out _), "acquire reached the driver");
        Assert.True(faults.TryTake(VulkanCall.QueuePresent, out _), "present reached the driver");
        Assert.True(faults.TryTake(VulkanCall.CreateSwapchain, out _), "create reached the driver");
        Assert.Equal((0, 0, 0), (destroys.Swapchains, destroys.Views, destroys.Semaphores));
    }

    private static void AssertDistinctLive(IEnumerable<nint> handles, int count)
    {
        nint[] all = handles.ToArray();
        Assert.Equal(count, all.Length);
        Assert.DoesNotContain((nint)0, all);
        Assert.Equal(count, all.Distinct().Count());
    }

    private static void Check(VkResult result, string call) => Assert.True(result == VkResult.Success, $"{call}: {result}");

    // The layer's report of an acquire from a retired swapchain, by its VUID's stable prefix (the layer renames VUIDs).
    private static bool Retired(string message) =>
        message.Contains("VUID-vkAcquireNextImageKHR-swapchain-", StringComparison.Ordinal) && message.Contains("retired", StringComparison.Ordinal);

    private void OnEveryPresentingAdapter(Action<Gpu> body) => OnEveryPresentingAdapter(fixture, false, null, body);

    private void OnEveryPresentingAdapter(Func<string, bool>? expected, Action<Gpu> body) => OnEveryPresentingAdapter(fixture, false, expected, body);

    // Runs the body once per adapter that can present to the class's raw surface, then checks that validation reported
    // no error during it, through every device's destruction (leaked children are reported there). With an expected
    // error, each adapter's run must report it, and it is the only error allowed. Each device is made without swapchain
    // maintenance1 when asked, so the fallback without present fences runs on any adapter.
    internal static void OnEveryPresentingAdapter(SurfaceFixture fixture, bool withoutMaintenance1, Func<string, bool>? expected, Action<Gpu> body)
    {
        RawSurface raw = fixture.Raw;
        int before = fixture.Sink.Entries.Count;
        List<string> ran = [];
        foreach (GpuDevices.Adapter adapter in raw.Presenting())
        {
            int start = fixture.Sink.Entries.Count;
            using (Gpu gpu = new(raw, adapter, withoutMaintenance1))
            {
                body(gpu);
                ran.Add($"{adapter.Candidate.Name} ({gpu.Plan.PresentMode}, {gpu.Plan.Format}, maintenance1 {gpu.Core.Extensions.SwapchainMaintenance1 ?? "off"})");
            }

            bool reported = expected is null || fixture.Sink.Entries.Skip(start).Any(e => e.Level == LogLevel.Error && expected(e.Message));
            Assert.True(reported, $"{adapter.Candidate.Name}: the expected validation error wasn't reported");
        }

        Assert.NotEmpty(ran);
        TestContext.Current.TestOutputHelper?.WriteLine("adapters: " + string.Join("; ", ran));
        string[] errors = [.. fixture.Sink.Entries.Skip(before).Where(e => e.Level == LogLevel.Error && expected?.Invoke(e.Message) != true).Select(e => e.Message)];
        Assert.True(errors.Length == 0, string.Join('\n', errors));
    }

    /// <summary>
    /// One validated instance, hidden window and raw surface for the swapchain classes (<see cref="SharedRawSurface"/>),
    /// so they create one instance rather than one per test: on the NVIDIA host every instance's driver scan uses up more
    /// of the process's static TLS, and once it runs out the NVIDIA driver no longer loads (#434). Tests of a collection
    /// run one at a time, so sharing is safe; each test checks only the validation messages logged while it ran. The cost:
    /// the validation layer stops repeating a message after 10 reports per instance (its duplicate-message limit; the
    /// instance is made without layer settings, so the limit stays), so once a regression has been reported ten times,
    /// later tests may not see it again. Whichever test meets a regression first fails, so the run still fails; which test
    /// that is depends on the order.
    /// </summary>
    public sealed class SurfaceFixture : IDisposable
    {
        private readonly Logger _logger;

        public SurfaceFixture()
        {
            Sink = new ValidationLog();
            (_logger, LogCategory category) = ValidationLog.Create(Sink);
            try
            {
                Raw = new RawSurface(category);

                // Instance creation is where the loader probes drivers. The only errors allowed here are the two of the
                // NVIDIA host's static-TLS failure (#434), which mean the RTX isn't enumerated; the tests then run on the
                // others.
                string[] errors = [.. Sink.Entries.Where(e => e.Level == LogLevel.Error && !IsLoaderTlsFailure(e.Message)).Select(e => e.Message)];
                Assert.True(errors.Length == 0, string.Join('\n', errors));
            }
            catch
            {
                Raw?.Dispose();
                _logger.Dispose();
                throw;
            }
        }

        internal ValidationLog Sink { get; }

        internal RawSurface Raw { get; }

        public void Dispose()
        {
            Raw.Dispose();
            _logger.Dispose();
        }

        // Exactly the loader's two messages for a NVIDIA driver library that couldn't be loaded into this process.
        private static bool IsLoaderTlsFailure(string message) =>
            message.StartsWith("Vulkan general message Loader Message (0): ", StringComparison.Ordinal)
            && (message.EndsWith(": cannot allocate memory in static TLS block", StringComparison.Ordinal)
                || message.Contains("loader_icd_scan: Failed loading library associated with ICD JSON libGLX_nvidia.so.0.", StringComparison.Ordinal));
    }

    // One device on a presenting adapter, with the extensions the device table picks, and the plan for the 64x64 test
    // window (mailbox where offered, so a hidden window's compositor never holds the frame loop back; FIFO otherwise).
    // It owns the swapchains it creates and disposes them after the queue is idle, so a failed assertion leaves none on
    // the class's surface (a test may dispose one itself first).
    internal sealed class Gpu : IDisposable
    {
        private readonly List<VulkanSwapchain> _swapchains = [];
        private bool _stuck;

        public Gpu(RawSurface raw, GpuDevices.Adapter adapter, bool withoutMaintenance1 = false)
        {
            Raw = raw;
            Adapter = adapter;
            Core = raw.CreateDevice(adapter, new VulkanCallGate(), withoutMaintenance1);
            Result<SurfaceFacts> facts = SurfaceQueries.Read(Core, raw.Surface, new VulkanCallGate());
            Assert.True(facts.IsSuccess, facts.ToString());
            Result<SwapchainPlan> plan = SwapchainPlanner.Plan(facts.Value, new VkExtent2D(64, 64), new SwapchainOptions { PresentMode = PresentMode.Mailbox });
            Assert.True(plan.IsSuccess, plan.ToString());
            Plan = plan.Value;
        }

        public RawSurface Raw { get; }

        public GpuDevices.Adapter Adapter { get; }

        public VulkanDeviceCore Core { get; }

        public SwapchainPlan Plan { get; }

        public VulkanSwapchain Create(int framesInFlight, VulkanCallGate? gate = null, VulkanSwapchain? old = null, VulkanSwapchain.DestroyCounts? destroys = null)
        {
            Result<VulkanSwapchain> created = VulkanSwapchain.Create(Core, Raw.Surface, Plan, old, framesInFlight, gate ?? new VulkanCallGate(), _timeout, destroys);
            Assert.True(created.IsSuccess, created.ToString());
            _swapchains.Add(created.Value);
            return created.Value;
        }

        // The swapchain's images straight from the driver, not through the code under test.
        public VkImage[] DriverImages(VulkanSwapchain swapchain)
        {
            Device device = Core.Device;
            int count = 0;
            Check(device.PInvoke.vkGetSwapchainImagesKHR.Invoke(device.Handle, swapchain.Handle, &count, null), "image count");
            var images = new VkImage[count];
            fixed (VkImage* pImages = images)
            {
                Check(device.PInvoke.vkGetSwapchainImagesKHR.Invoke(device.Handle, swapchain.Handle, &count, pImages), "images");
            }

            return images[..count];
        }

        public void WaitIdle() => Assert.True(TryWaitIdle(), "the queue didn't go idle within the wait: a submit waits forever");

        // vkDeviceWaitIdle has no timeout, so the queue is waited on first with a bound: an empty submit's fence signals
        // once all earlier work on the queue is complete. If it doesn't, the device is stuck and stays alive (a failing
        // run leaks it rather than hanging the test host).
        public bool TryWaitIdle()
        {
            if (_stuck)
            {
                return false;
            }

            Device device = Core.Device;
            VkFenceCreateInfo info = new();
            VkFence fence = default;
            Check(device.PInvoke.vkCreateFence.Invoke(device.Handle, &info, null, &fence), "vkCreateFence");
            Check(device.PInvoke.vkQueueSubmit2.Invoke(Core.Queue, 0, null, fence), "vkQueueSubmit2");
            _stuck = device.PInvoke.vkWaitForFences.Invoke(device.Handle, 1, &fence, true, _timeout) != VkResult.Success;
            if (_stuck)
            {
                return false;
            }

            device.PInvoke.vkDestroyFence.Invoke(device.Handle, fence, null);
            Check(device.PInvoke.vkDeviceWaitIdle.Invoke(device.Handle), "vkDeviceWaitIdle");
            return true;
        }

        public void Dispose()
        {
            if (Core.IsDisposed || !TryWaitIdle())
            {
                return;
            }

            for (int i = _swapchains.Count - 1; i >= 0; i--)
            {
                _swapchains[i].Dispose();
            }

            Core.Dispose();
        }
    }

    // The frame work the swapchain component doesn't own (#311's frame slots): per slot a fence, a command pool and one
    // command buffer that moves the acquired image to PRESENT_SRC, waiting on the slot's acquire semaphore and
    // signalling the image's render-finished semaphore, both at COLOR_ATTACHMENT_OUTPUT as M0 does
    // (samples/HelloWindow/FrameRenderer.cs:114-116, :318-333).
    internal sealed class FrameLoop : IDisposable
    {
        private readonly Gpu _gpu;
        private readonly VkFence[] _fences;
        private readonly VkCommandPool[] _pools;
        private readonly VkCommandBuffer[] _commands;

        public FrameLoop(Gpu gpu, int slots)
        {
            _gpu = gpu;
            _fences = new VkFence[slots];
            _pools = new VkCommandPool[slots];
            _commands = new VkCommandBuffer[slots];
            Device device = gpu.Core.Device;
            for (int slot = 0; slot < slots; slot++)
            {
                VkFenceCreateInfo fence = new() { Flags = VkFenceCreateFlags.SignaledBit };
                VkFence createdFence = default;
                Check(device.PInvoke.vkCreateFence.Invoke(device.Handle, &fence, null, &createdFence), "vkCreateFence");
                _fences[slot] = createdFence;
                VkCommandPoolCreateInfo pool = new() { QueueFamilyIndex = gpu.Core.QueueFamily };
                VkCommandPool createdPool = default;
                Check(device.PInvoke.vkCreateCommandPool.Invoke(device.Handle, &pool, null, &createdPool), "vkCreateCommandPool");
                _pools[slot] = createdPool;
                VkCommandBufferAllocateInfo allocate = new() { CommandPool = createdPool, Level = VkCommandBufferLevel.Primary };

                // BRUTAL makes commandBufferCount internal; write it at its explicit offset, 28
                // (Brutal.Vulkan/Generated/Structs/VkCommandBufferAllocateInfo.gen.cs:51-52), as GpuValidation does.
                *(int*)((byte*)&allocate + 28) = 1;
                VkCommandBuffer commands = default;
                Check(device.PInvoke.vkAllocateCommandBuffers.Invoke(device.Handle, &allocate, &commands), "vkAllocateCommandBuffers");
                _commands[slot] = commands;
            }
        }

        /// <summary>Full frames: wait, acquire, submit, present, each asserted to succeed.</summary>
        public void Run(VulkanSwapchain swapchain, int frames)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                int slot = frame % _fences.Length;
                WaitSlot(slot);
                int image = AcquireAndSubmit(swapchain, slot);
                Assert.Equal(new BackendPresent(SwapchainStatus.Success, 0), swapchain.Present(_gpu.Core.Queue, image));
            }
        }

        public void WaitSlot(int slot)
        {
            Device device = _gpu.Core.Device;
            VkFence fence = _fences[slot];
            Check(device.PInvoke.vkWaitForFences.Invoke(device.Handle, 1, &fence, true, unchecked((nint)WaitNanoseconds)), "vkWaitForFences");
        }

        public int AcquireAndSubmit(VulkanSwapchain swapchain, int slot)
        {
            BackendAcquire acquired = swapchain.Acquire(slot, _timeout);
            Assert.Equal(SwapchainStatus.Success, acquired.Status);
            Assert.Equal(0, acquired.NativeCode);
            Assert.InRange(acquired.ImageIndex, 0, swapchain.ImageCount - 1);
            Submit(swapchain, slot, acquired.ImageIndex);
            return acquired.ImageIndex;
        }

        // Waits on the slot's acquire semaphore in a submit of its own, so it is unsignalled again; the slot is idle after.
        public void Consume(VulkanSwapchain swapchain, int slot)
        {
            Device device = _gpu.Core.Device;
            WaitSlot(slot);
            VkSemaphoreSubmitInfo wait = new() { Semaphore = swapchain.GetAcquireSemaphore(slot), StageMask = VkPipelineStageFlags2.AllCommandsBit };
            VkSubmitInfo2 submit = new() { WaitSemaphoreInfoCount = 1, WaitSemaphoreInfos = &wait };
            VkFence fence = _fences[slot];
            Check(device.PInvoke.vkResetFences.Invoke(device.Handle, 1, &fence), "vkResetFences");
            Check(device.PInvoke.vkQueueSubmit2.Invoke(_gpu.Core.Queue, 1, &submit, fence), "vkQueueSubmit2");
            WaitSlot(slot);
        }

        public void Submit(VulkanSwapchain swapchain, int slot, int image)
        {
            Device device = _gpu.Core.Device;
            VkCommandBuffer commands = _commands[slot];
            Check(device.PInvoke.vkResetCommandPool.Invoke(device.Handle, _pools[slot], VkCommandPoolResetFlags.None), "vkResetCommandPool");
            VkCommandBufferBeginInfo begin = new() { Flags = VkCommandBufferUsageFlags.OneTimeSubmitBit };
            Check(device.PInvoke.vkBeginCommandBuffer.Invoke(commands, &begin), "vkBeginCommandBuffer");
            VkImageMemoryBarrier2 toPresent = new()
            {
                SrcStageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit,
                DstStageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit,
                OldLayout = VkImageLayout.Undefined,
                NewLayout = VkImageLayout.PresentSrcKHR,
                SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                Image = swapchain.GetImage(image),
                SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
            };
            VkDependencyInfo dependency = new() { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &toPresent };
            device.PInvoke.vkCmdPipelineBarrier2.Invoke(commands, &dependency);
            Check(device.PInvoke.vkEndCommandBuffer.Invoke(commands), "vkEndCommandBuffer");

            VkSemaphoreSubmitInfo wait = new() { Semaphore = swapchain.GetAcquireSemaphore(slot), StageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit };
            VkSemaphoreSubmitInfo signal = new() { Semaphore = swapchain.GetRenderFinished(image), StageMask = VkPipelineStageFlags2.ColorAttachmentOutputBit };
            VkCommandBufferSubmitInfo commandInfo = new() { CommandBuffer = commands };
            VkSubmitInfo2 submit = new()
            {
                WaitSemaphoreInfoCount = 1,
                WaitSemaphoreInfos = &wait,
                CommandBufferInfoCount = 1,
                CommandBufferInfos = &commandInfo,
                SignalSemaphoreInfoCount = 1,
                SignalSemaphoreInfos = &signal,
            };
            VkFence fence = _fences[slot];
            Check(device.PInvoke.vkResetFences.Invoke(device.Handle, 1, &fence), "vkResetFences");
            Check(device.PInvoke.vkQueueSubmit2.Invoke(_gpu.Core.Queue, 1, &submit, fence), "vkQueueSubmit2");
        }

        public void Dispose()
        {
            if (!_gpu.TryWaitIdle())
            {
                return;
            }

            Device device = _gpu.Core.Device;
            for (int slot = 0; slot < _fences.Length; slot++)
            {
                device.PInvoke.vkDestroyCommandPool.Invoke(device.Handle, _pools[slot], null);
                device.PInvoke.vkDestroyFence.Invoke(device.Handle, _fences[slot], null);
            }
        }
    }
}
