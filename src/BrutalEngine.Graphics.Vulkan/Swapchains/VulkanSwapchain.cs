using System;
using Brutal.VulkanApi;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Vulkan.Devices;

namespace BrutalEngine.Graphics.Vulkan.Swapchains;

/// <summary>
/// One Vulkan swapchain on a raw surface (docs/design/graphics-vulkan.md section 4): the swapchain, a view and a
/// render-finished semaphore per image, and an acquire semaphore per frame slot; it acquires and presents through the
/// raw device function table.
/// </summary>
/// <remarks>
/// <para>
/// The render-finished semaphores are per image, not per slot: a semaphore a present waits on can be signalled again
/// only once that image has been acquired again, which per-slot semaphores can't guarantee
/// (<c>samples/HelloWindow/Swapchain.cs:5-9</c>). The acquire semaphores are per slot, because the image an acquire
/// returns isn't known until it returns.
/// </para>
/// <para>
/// Acquire and present call <c>vkAcquireNextImageKHR</c> and <c>vkQueuePresentKHR</c> through the raw table
/// (<c>Brutal.Vulkan/Generated/Handles/VkDevice.gen.cs:247-248</c>), because BRUTAL's wrappers throw on
/// <c>VK_ERROR_OUT_OF_DATE_KHR</c> (upstream issue draft #1), and the raw present skips BRUTAL's queue lock (section 13,
/// rule 6). Their results map through <see cref="VkCheck.SwapchainStatusOf"/>, and pass through the gate so a lost
/// device is detected in one place. Once the gate is lost they return <see cref="SwapchainStatus.DeviceLost"/> without
/// calling the driver.
/// </para>
/// <para>
/// The swapchain owns its images; <see cref="Dispose"/> destroys the semaphores, the views and the swapchain, never the
/// surface. The owner disposes it once the device is idle and before the device.
/// </para>
/// <para>
/// Not thread-safe: the device's owner thread creates, uses and disposes it. Creation allocates (this object and its
/// arrays); <see cref="Acquire"/>, <see cref="Present"/> and the getters allocate nothing.
/// </para>
/// </remarks>
internal sealed unsafe class VulkanSwapchain : IDisposable
{
    /// <summary>The most frames in flight a device has (<c>BackendDeviceRequest.FramesInFlight</c>).</summary>
    internal const int MaxFramesInFlight = 3;

    private readonly VulkanDeviceCore _core;
    private readonly VulkanCallGate _gate;
    private readonly DestroyCounts? _destroys;
    private readonly VkSemaphore[] _acquired;
    private VkImage[] _images = [];
    private VkImageView[] _views = [];
    private VkSemaphore[] _renderFinished = [];
    private VkSwapchainKHR _handle;
    private bool _disposed;

    private VulkanSwapchain(VulkanDeviceCore core, VulkanCallGate gate, in SwapchainPlan plan, VkSwapchainKHR handle, int framesInFlight, DestroyCounts? destroys)
    {
        _core = core;
        _gate = gate;
        Plan = plan;
        _handle = handle;
        _acquired = new VkSemaphore[framesInFlight];
        _destroys = destroys;
    }

    /// <summary>Gets the plan the swapchain was created with: its format, extent, usage and present mode.</summary>
    public SwapchainPlan Plan { get; }

    /// <summary>Gets the swapchain's handle; the null handle once disposed.</summary>
    public VkSwapchainKHR Handle => _handle;

    /// <summary>Gets the number of images the driver created; at least the plan's image count.</summary>
    public int ImageCount => _images.Length;

    /// <summary>Gets the number of frame slots, each with its own acquire semaphore.</summary>
    public int FramesInFlight => _acquired.Length;

    /// <summary>Gets whether <see cref="Dispose"/> has run.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>Creates a swapchain on a surface, or recreates one.</summary>
    /// <param name="core">The device; it outlives the swapchain.</param>
    /// <param name="surface">The surface, created from the device's instance; not the null handle.</param>
    /// <param name="plan">The planner's decisions (<see cref="SwapchainPlanner.Plan"/>), the surface's current transform included.</param>
    /// <param name="oldSwapchain">
    /// The swapchain being replaced on the same surface, or <see langword="null"/>. It is retired either way and stays the
    /// caller's to dispose, whether this succeeds or fails, once the device is idle.
    /// </param>
    /// <param name="framesInFlight">The device's frame slots, 1 to 3.</param>
    /// <param name="gate">The device's gate.</param>
    /// <param name="destroys">A test's counters of destroyed objects, or <see langword="null"/>.</param>
    /// <returns>
    /// The swapchain, owned by the caller. Or a failure, after which everything this call created has been destroyed:
    /// <see cref="ErrorKind.Unavailable"/> when the gate is lost (no driver call); <see cref="ErrorKind.Cancelled"/>
    /// ("surface extent is zero") for a plan with no area (no driver call); or the first failed call's error
    /// (<see cref="VkCheck.Error"/>), such as <see cref="ErrorKind.OutOfMemory"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="core"/> or <paramref name="gate"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="surface"/> is the null handle.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="framesInFlight"/> is outside 1 to 3.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="core"/> or <paramref name="oldSwapchain"/> is disposed.</exception>
    public static Result<VulkanSwapchain> Create(
        VulkanDeviceCore core,
        VkSurfaceKHR surface,
        in SwapchainPlan plan,
        VulkanSwapchain? oldSwapchain,
        int framesInFlight,
        VulkanCallGate gate,
        DestroyCounts? destroys = null)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(gate);
        if (surface.IsNull())
        {
            throw new ArgumentException("The surface is the null handle.", nameof(surface));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(framesInFlight, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(framesInFlight, MaxFramesInFlight);
        ObjectDisposedException.ThrowIf(oldSwapchain is { IsDisposed: true }, typeof(VulkanSwapchain));

        Device device = core.Device;
        if (gate.IsLost)
        {
            return new EngineError(ErrorKind.Unavailable, "device is lost", (int)VkResult.ErrorDeviceLost);
        }

        if (plan.Width <= 0 || plan.Height <= 0)
        {
            return new EngineError(ErrorKind.Cancelled, "surface extent is zero");
        }

        // Sound: info and handle are stack locals that outlive the call; the driver copies the description.
        VkSwapchainCreateInfoKHR info = new()
        {
            Surface = surface,
            MinImageCount = plan.ImageCount,
            ImageFormat = plan.Format,
            ImageColorSpace = plan.ColorSpace,
            ImageExtent = new VkExtent2D(plan.Width, plan.Height),
            ImageArrayLayers = 1,
            ImageUsage = plan.Usage,
            ImageSharingMode = VkSharingMode.Exclusive,
            PreTransform = plan.PreTransform,
            CompositeAlpha = plan.CompositeAlpha,
            PresentMode = plan.VkPresentMode,
            Clipped = true,
            OldSwapchain = oldSwapchain?.Handle ?? default,
        };
        VkSwapchainKHR handle = default;
        VkResult result = gate.TryTakeFault(VulkanCall.CreateSwapchain, out VkResult injected) ? injected : device.PInvoke.vkCreateSwapchainKHR.Invoke(device.Handle, &info, null, &handle);
        Result created = gate.Check(result, VulkanCall.CreateSwapchain);
        if (created.IsFailure)
        {
            return created.Error;
        }

        VulkanSwapchain swapchain = new(core, gate, plan, handle, framesInFlight, destroys);
        Result filled = swapchain.CreateImageObjects(device);
        if (filled.IsFailure)
        {
            swapchain.Dispose();
            return filled.Error;
        }

        return swapchain;
    }

    /// <summary>Gets an image; owned by the swapchain.</summary>
    /// <param name="index">The image's index, from 0 to <see cref="ImageCount"/> minus 1.</param>
    /// <returns>The image.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    public VkImage GetImage(int index) => _images[index];

    /// <summary>Gets an image's colour view.</summary>
    /// <param name="index">The image's index.</param>
    /// <returns>The view, of the plan's format.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    public VkImageView GetView(int index) => _views[index];

    /// <summary>Gets an image's render-finished semaphore: the frame's submit signals it, <see cref="Present"/> waits on it.</summary>
    /// <param name="index">The image's index.</param>
    /// <returns>The semaphore.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    public VkSemaphore GetRenderFinished(int index) => _renderFinished[index];

    /// <summary>Gets a slot's acquire semaphore: <see cref="Acquire"/> signals it, the frame's submit waits on it.</summary>
    /// <param name="slot">The frame slot, from 0 to <see cref="FramesInFlight"/> minus 1.</param>
    /// <returns>The semaphore.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="slot"/> is out of range.</exception>
    public VkSemaphore GetAcquireSemaphore(int slot) => _acquired[slot];

    /// <summary>Acquires the next image for a frame slot, signalling the slot's acquire semaphore when it is ready.</summary>
    /// <param name="slot">The frame slot, from 0 to <see cref="FramesInFlight"/> minus 1.</param>
    /// <param name="timeout">
    /// The wait for an available image in nanoseconds, BRUTAL's <c>nint</c> for <c>uint64_t</c>: the caller passes
    /// <c>GpuTimeout</c> as <c>unchecked((nint)ns)</c> (section 13, rule 7).
    /// </param>
    /// <returns>
    /// The status (<see cref="VkCheck.SwapchainStatusOf"/>; a timeout is <see cref="SwapchainStatus.OutOfDate"/>, not a
    /// loss), the image index for <see cref="SwapchainStatus.Success"/> and <see cref="SwapchainStatus.Suboptimal"/> and
    /// -1 otherwise, and the <c>VkResult</c> as the native code.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The swapchain or its device is disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is out of range.</exception>
    /// <remarks>Allocates nothing.</remarks>
    public BackendAcquire Acquire(int slot, nint timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)slot >= (uint)_acquired.Length)
        {
            ThrowOutOfRange(nameof(slot), slot);
        }

        if (_gate.IsLost)
        {
            return new BackendAcquire(SwapchainStatus.DeviceLost, -1, (int)VkResult.ErrorDeviceLost);
        }

        Device device = _core.Device;
        int index = -1;

        // Sound: index is a local the driver writes before returning.
        VkResult result = _gate.TryTakeFault(VulkanCall.AcquireNextImage, out VkResult injected) ? injected
            : device.PInvoke.vkAcquireNextImageKHR.Invoke(device.Handle, _handle, timeout, _acquired[slot], default, &index);

        // Only to mark a lost device; the status below says what happened.
        _ = _gate.Check(result, VulkanCall.AcquireNextImage);
        SwapchainStatus status = VkCheck.SwapchainStatusOf(result, acquire: true);
        return new BackendAcquire(status, status is SwapchainStatus.Success or SwapchainStatus.Suboptimal ? index : -1, (int)result);
    }

    /// <summary>Presents an acquired image on a queue, waiting on the image's render-finished semaphore.</summary>
    /// <param name="queue">The device's queue, which can present to the surface.</param>
    /// <param name="imageIndex">The image's index, from an acquire whose frame signals its render-finished semaphore.</param>
    /// <returns>The status (<see cref="VkCheck.SwapchainStatusOf"/>) and the <c>VkResult</c> as the native code.</returns>
    /// <exception cref="ObjectDisposedException">The swapchain or its device is disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="imageIndex"/> is out of range.</exception>
    /// <remarks>Allocates nothing.</remarks>
    public BackendPresent Present(VkQueue queue, int imageIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)imageIndex >= (uint)_images.Length)
        {
            ThrowOutOfRange(nameof(imageIndex), imageIndex);
        }

        if (_gate.IsLost)
        {
            return new BackendPresent(SwapchainStatus.DeviceLost, (int)VkResult.ErrorDeviceLost);
        }

        Device device = _core.Device;
        VkSwapchainKHR handle = _handle;
        VkSemaphore wait = _renderFinished[imageIndex];

        // Sound: every pointer is to a local or a parameter of this method, which outlive the call.
        VkPresentInfoKHR info = new() { WaitSemaphoreCount = 1, WaitSemaphores = &wait, SwapchainCount = 1, Swapchains = &handle, ImageIndices = &imageIndex };
        VkResult result = _gate.TryTakeFault(VulkanCall.QueuePresent, out VkResult injected) ? injected : device.PInvoke.vkQueuePresentKHR.Invoke(queue, &info);

        // Only to mark a lost device; the status below says what happened.
        _ = _gate.Check(result, VulkanCall.QueuePresent);
        return new BackendPresent(VkCheck.SwapchainStatusOf(result, acquire: false), (int)result);
    }

    /// <summary>Destroys the acquire semaphores, then each image's semaphore and view, then the swapchain; once.</summary>
    /// <exception cref="ObjectDisposedException">The device was disposed first (misuse); nothing is destroyed.</exception>
    /// <remarks>The device must be idle. Never destroys the surface or the device.</remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Device device = _core.Device;
        foreach (VkSemaphore semaphore in _acquired)
        {
            DestroySemaphore(device, semaphore);
        }

        for (int i = _images.Length - 1; i >= 0; i--)
        {
            DestroySemaphore(device, _renderFinished[i]);
            if (!_views[i].IsNull())
            {
                device.PInvoke.vkDestroyImageView.Invoke(device.Handle, _views[i], null);
                _destroys?.OnView();
            }
        }

        device.PInvoke.vkDestroySwapchainKHR.Invoke(device.Handle, _handle, null);
        _destroys?.OnSwapchain();
        _handle = default;
    }

    private static void ThrowOutOfRange(string name, int value) =>
        throw new ArgumentOutOfRangeException(name, value, "Out of the swapchain's range.");

    // Reads the images with the two-call pattern (their number is fixed, so VK_INCOMPLETE is a failure), then creates a
    // view and a render-finished semaphore per image and an acquire semaphore per slot. Each object is stored as soon
    // as it exists, so Dispose destroys exactly what was created.
    private Result CreateImageObjects(Device device)
    {
        int count = 0;
        VkResult result = _gate.TryTakeFault(VulkanCall.GetSwapchainImages, out VkResult injected) ? injected : device.PInvoke.vkGetSwapchainImagesKHR.Invoke(device.Handle, _handle, &count, null);
        if (result == VkResult.Success)
        {
            var images = new VkImage[count];

            // Sound: count is a local and the array is pinned for the call; the driver writes at most count images.
            fixed (VkImage* pImages = images)
            {
                result = _gate.TryTakeFault(VulkanCall.GetSwapchainImages, out injected) ? injected : device.PInvoke.vkGetSwapchainImagesKHR.Invoke(device.Handle, _handle, &count, pImages);
            }

            _images = count < images.Length ? images[..count] : images;
        }

        Result read = _gate.Check(result, VulkanCall.GetSwapchainImages);
        if (read.IsFailure)
        {
            _images = [];
            return read;
        }

        _views = new VkImageView[_images.Length];
        _renderFinished = new VkSemaphore[_images.Length];
        for (int i = 0; i < _images.Length; i++)
        {
            // Sound: the description and view are stack locals that outlive the call.
            VkImageViewCreateInfo description = new()
            {
                Image = _images[i],
                ViewType = VkImageViewType._2D,
                Format = Plan.Format,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
            };
            VkImageView view = default;
            result = _gate.TryTakeFault(VulkanCall.CreateImageView, out injected) ? injected : device.PInvoke.vkCreateImageView.Invoke(device.Handle, &description, null, &view);
            Result step = _gate.Check(result, VulkanCall.CreateImageView);
            if (step.IsFailure)
            {
                return step;
            }

            _views[i] = view;
            step = CreateSemaphore(device, out _renderFinished[i]);
            if (step.IsFailure)
            {
                return step;
            }
        }

        for (int slot = 0; slot < _acquired.Length; slot++)
        {
            Result step = CreateSemaphore(device, out _acquired[slot]);
            if (step.IsFailure)
            {
                return step;
            }
        }

        return Result.Success;
    }

    private Result CreateSemaphore(Device device, out VkSemaphore semaphore)
    {
        VkSemaphoreCreateInfo description = new();
        VkSemaphore created = default;

        // Sound: the description and the handle are stack locals that outlive the call.
        VkResult result = _gate.TryTakeFault(VulkanCall.CreateSemaphore, out VkResult injected) ? injected : device.PInvoke.vkCreateSemaphore.Invoke(device.Handle, &description, null, &created);
        semaphore = result == VkResult.Success ? created : default;
        return _gate.Check(result, VulkanCall.CreateSemaphore);
    }

    private void DestroySemaphore(Device device, VkSemaphore semaphore)
    {
        if (!semaphore.IsNull())
        {
            device.PInvoke.vkDestroySemaphore.Invoke(device.Handle, semaphore, null);
            _destroys?.OnSemaphore();
        }
    }

    /// <summary>A test's counters of the objects a swapchain destroyed.</summary>
    internal sealed class DestroyCounts
    {
        /// <summary>Gets the number of swapchains destroyed.</summary>
        public int Swapchains { get; private set; }

        /// <summary>Gets the number of image views destroyed.</summary>
        public int Views { get; private set; }

        /// <summary>Gets the number of semaphores destroyed.</summary>
        public int Semaphores { get; private set; }

        /// <summary>Records a destroyed swapchain.</summary>
        internal void OnSwapchain() => Swapchains++;

        /// <summary>Records a destroyed view.</summary>
        internal void OnView() => Views++;

        /// <summary>Records a destroyed semaphore.</summary>
        internal void OnSemaphore() => Semaphores++;
    }
}
