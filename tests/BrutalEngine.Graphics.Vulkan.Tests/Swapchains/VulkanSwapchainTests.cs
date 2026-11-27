using System;
using System.Collections.Generic;
using System.Linq;
using Brutal.VulkanApi;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Vulkan.Devices;
using BrutalEngine.Graphics.Vulkan.Swapchains;
using BrutalEngine.Graphics.Vulkan.Tests.Devices;
using BrutalEngine.Graphics.Vulkan.Tests.Validation;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Swapchains;

// docs/design/graphics-vulkan.md section 4: the swapchain component on a raw surface, on every adapter that can present
// to it (lavapipe headless; the RTX too when SDL_VIDEO_DRIVER=wayland is set for a local run), with validation on.
[Trait("Category", "Gpu")]
public sealed unsafe class VulkanSwapchainTests(VulkanSwapchainTests.SurfaceFixture fixture) : IClassFixture<VulkanSwapchainTests.SurfaceFixture>
{
    private const string ZeroExtent = "surface extent is zero";

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

            Result<VulkanSwapchain> result = VulkanSwapchain.Create(gpu.Core, gpu.Raw.Surface, gpu.Plan, null, Frames, new VulkanCallGate { Faults = faults }, destroys);

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
                gpu.Core, gpu.Raw.Surface, gpu.Plan with { Width = width, Height = height }, null, 2, new VulkanCallGate { Faults = faults }, destroys);

            Assert.Equal(new EngineError(ErrorKind.Cancelled, ZeroExtent), result.Error);
            Assert.True(faults.TryTake(VulkanCall.CreateSwapchain, out _), "a driver call was made");
            Assert.Equal((0, 0, 0), (destroys.Swapchains, destroys.Views, destroys.Semaphores));
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

            Assert.Throws<ArgumentNullException>("core", () => VulkanSwapchain.Create(null!, surface, gpu.Plan, null, 2, gate));
            Assert.Throws<ArgumentNullException>("gate", () => VulkanSwapchain.Create(gpu.Core, surface, gpu.Plan, null, 2, null!));
            Assert.Throws<ArgumentException>("surface", () => VulkanSwapchain.Create(gpu.Core, default, gpu.Plan, null, 2, gate));
            Assert.Throws<ArgumentOutOfRangeException>("framesInFlight", () => VulkanSwapchain.Create(gpu.Core, surface, gpu.Plan, null, 0, gate));
            Assert.Throws<ArgumentOutOfRangeException>("framesInFlight", () => VulkanSwapchain.Create(gpu.Core, surface, gpu.Plan, null, 4, gate));
            Assert.Throws<ObjectDisposedException>(() => VulkanSwapchain.Create(gpu.Core, surface, gpu.Plan, disposed, 2, gate));
            Assert.Throws<ObjectDisposedException>(() => VulkanSwapchain.Create(gone, surface, gpu.Plan, null, 2, gate));
            Assert.True(faults.TryTake(VulkanCall.CreateSwapchain, out _), "a driver call was made");

            using VulkanSwapchain swapchain = gpu.Create(2);
            Assert.Throws<IndexOutOfRangeException>(() => swapchain.GetImage(swapchain.ImageCount));
            Assert.Throws<IndexOutOfRangeException>(() => swapchain.GetView(-1));
            Assert.Throws<IndexOutOfRangeException>(() => swapchain.GetAcquireSemaphore(2));
        });
    }

    private static void AssertDistinctLive(IEnumerable<nint> handles, int count)
    {
        nint[] all = handles.ToArray();
        Assert.Equal(count, all.Length);
        Assert.DoesNotContain((nint)0, all);
        Assert.Equal(count, all.Distinct().Count());
    }

    private static void Check(VkResult result, string call) => Assert.True(result == VkResult.Success, $"{call}: {result}");

    // Runs the body once per adapter that can present to the class's raw surface, then checks that validation reported
    // no error during it, through every device's destruction (leaked children are reported there).
    private void OnEveryPresentingAdapter(Action<Gpu> body)
    {
        RawSurface raw = fixture.Raw;
        int before = fixture.Sink.Entries.Count;
        List<string> ran = [];
        foreach (GpuDevices.Adapter adapter in raw.Presenting())
        {
            using Gpu gpu = new(raw, adapter);
            body(gpu);
            ran.Add($"{adapter.Candidate.Name} ({gpu.Plan.PresentMode}, {gpu.Plan.Format}, maintenance1 {gpu.Core.Extensions.SwapchainMaintenance1 ?? "off"})");
        }

        Assert.NotEmpty(ran);
        TestContext.Current.TestOutputHelper?.WriteLine("adapters: " + string.Join("; ", ran));
        string[] errors = [.. fixture.Sink.Entries.Skip(before).Where(e => e.Level == LogLevel.Error).Select(e => e.Message)];
        Assert.True(errors.Length == 0, string.Join('\n', errors));
    }

    /// <summary>
    /// One validated instance, hidden window and raw surface for the whole class, so the class creates one instance
    /// rather than one per test: on the NVIDIA host every instance's driver scan uses up more of the process's static TLS,
    /// and once it runs out the NVIDIA driver no longer loads (#434). Tests run one at a time within a class, so sharing is
    /// safe; each test checks only the validation messages logged while it ran. The cost: the validation layer stops
    /// repeating a message after 10 reports per instance, so once one test has failed on a message ten times, later
    /// tests in the class may not see it again. The first test to meet a regression still fails.
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
            }
            catch
            {
                _logger.Dispose();
                throw;
            }

            // Instance creation is where the loader probes drivers. The only errors allowed here are the two of the NVIDIA
            // host's static-TLS failure (#434), which mean the RTX isn't enumerated; the tests then run on the others.
            string[] errors = [.. Sink.Entries.Where(e => e.Level == LogLevel.Error && !IsLoaderTlsFailure(e.Message)).Select(e => e.Message)];
            Assert.True(errors.Length == 0, string.Join('\n', errors));
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
    private sealed class Gpu : IDisposable
    {
        public Gpu(RawSurface raw, GpuDevices.Adapter adapter)
        {
            Raw = raw;
            Adapter = adapter;
            Core = raw.CreateDevice(adapter, new VulkanCallGate());
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
            Result<VulkanSwapchain> created = VulkanSwapchain.Create(Core, Raw.Surface, Plan, old, framesInFlight, gate ?? new VulkanCallGate(), destroys);
            Assert.True(created.IsSuccess, created.ToString());
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

        public void WaitIdle() => Check(Core.Device.PInvoke.vkDeviceWaitIdle.Invoke(Core.Device.Handle), "vkDeviceWaitIdle");

        public void Dispose()
        {
            if (!Core.IsDisposed)
            {
                WaitIdle();
                Core.Dispose();
            }
        }
    }
}
