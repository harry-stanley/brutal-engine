using Brutal.VulkanApi;
using BrutalEngine.Core;

namespace BrutalEngine.Graphics.Vulkan.Swapchains;

/// <summary>
/// A swapchain's present fences with swapchain maintenance1 (docs/design/graphics-vulkan.md section 4, "Present
/// fences"): one fence per image, created signalled, that each present of the image carries
/// (<c>VkSwapchainPresentFenceInfoKHR</c>), so the swapchain can wait, bounded, for its presents before destroying what
/// they use.
/// </summary>
/// <remarks>
/// <para>
/// A fence is <em>armed</em> while it is signalled or an enqueued present will signal it: a new fence is armed;
/// <see cref="BeforePresent"/> waits for it and resets it, which disarms it; <see cref="AfterPresent"/> arms it again
/// only when the present was enqueued. The specification enqueues a present that returns <c>SUCCESS</c>,
/// <c>SUBOPTIMAL_KHR</c>, <c>ERROR_OUT_OF_DATE_KHR</c>, <c>ERROR_SURFACE_LOST_KHR</c> or
/// <c>ERROR_FULL_SCREEN_EXCLUSIVE_MODE_LOST_EXT</c>; any other result, or an injected one that replaced the call, leaves
/// the reset fence unsignalled for good. Only armed fences are waited for, so such a fence never turns a healthy GPU's
/// destroy into a timeout.
/// </para>
/// <para>
/// Every wait is bounded by the timeout it is given, <c>GpuTimeout</c>. A wait that fails without a reported loss (a
/// timeout, or out of memory) can't prove the presents finished, so it marks the gate lost as a timeout
/// (<see cref="VulkanCallGate.MarkLost"/>): a hung GPU, by the graphics note's <c>GpuTimeout</c> rule.
/// <c>VK_ERROR_DEVICE_LOST</c> is a reported loss, which the gate's check marks.
/// </para>
/// <para>
/// Owned by one <see cref="VulkanSwapchain"/>, which destroys the fences. Not thread-safe: the swapchain's owner thread
/// uses it. Creation allocates its arrays; <see cref="BeforePresent"/>, <see cref="AfterPresent"/> and <see cref="Get"/>
/// allocate nothing.
/// </para>
/// </remarks>
internal sealed unsafe class PresentFences
{
    private readonly VkFence[] _fences;
    private readonly bool[] _armed;
    private readonly VkFence[] _waitList;

    /// <summary>Creates the set for a number of images, with no fence created yet (<see cref="CreateAll"/>).</summary>
    /// <param name="count">The swapchain's image count; not negative.</param>
    public PresentFences(int count)
    {
        _fences = new VkFence[count];
        _armed = new bool[count];
        _waitList = new VkFence[count];
    }

    /// <summary>Gets an image's fence; the null handle before it is created.</summary>
    /// <param name="image">The image's index.</param>
    /// <returns>The fence.</returns>
    /// <exception cref="System.IndexOutOfRangeException"><paramref name="image"/> is out of range.</exception>
    public VkFence Get(int image) => _fences[image];

    /// <summary>Creates every image's fence, signalled, storing each as soon as it exists, through the gate.</summary>
    /// <param name="device">The swapchain's device.</param>
    /// <param name="gate">The device's gate.</param>
    /// <returns>Success, or the failed creation's error; the fences created before it stay for <see cref="Destroy"/>.</returns>
    public Result CreateAll(Device device, VulkanCallGate gate)
    {
        VkFenceCreateInfo description = new() { Flags = VkFenceCreateFlags.SignaledBit };
        for (int i = 0; i < _fences.Length; i++)
        {
            VkFence fence = default;

            // Sound: the description and the handle are stack locals that outlive the call.
            VkResult result = gate.TryTakeFault(VulkanCall.CreateFence, out VkResult injected) ? injected : device.PInvoke.vkCreateFence.Invoke(device.Handle, &description, null, &fence);
            Result created = gate.Check(result, VulkanCall.CreateFence);
            if (created.IsFailure)
            {
                return created;
            }

            _fences[i] = fence;
            _armed[i] = true;
        }

        return Result.Success;
    }

    /// <summary>Waits, bounded, for an image's fence when it is armed, then resets it, ready for the image's present.</summary>
    /// <param name="device">The swapchain's device.</param>
    /// <param name="gate">The device's gate.</param>
    /// <param name="image">The image about to be presented.</param>
    /// <param name="timeout">The wait in nanoseconds, as BRUTAL's <c>nint</c> for <c>uint64_t</c>.</param>
    /// <returns>
    /// Success, after which the fence is unsignalled and disarmed; or the failed wait's or reset's error. After a failed
    /// reset the fence is still armed.
    /// </returns>
    public Result BeforePresent(Device device, VulkanCallGate gate, int image, nint timeout)
    {
        VkFence fence = _fences[image];
        if (_armed[image])
        {
            Result waited = Wait(device, gate, &fence, 1, timeout);
            if (waited.IsFailure)
            {
                return waited;
            }
        }

        // Sound: the handle is a stack local that outlives the call. Reset only after the wait: a fence reset while its
        // present is pending would never be waited for.
        VkResult result = gate.TryTakeFault(VulkanCall.ResetFences, out VkResult injected) ? injected : device.PInvoke.vkResetFences.Invoke(device.Handle, 1, &fence);
        Result reset = gate.Check(result, VulkanCall.ResetFences);
        if (reset.IsSuccess)
        {
            _armed[image] = false;
        }

        return reset;
    }

    /// <summary>Arms an image's fence again when its present was enqueued with it.</summary>
    /// <param name="image">The image that was presented.</param>
    /// <param name="result">The present's result.</param>
    /// <param name="injected">Whether <paramref name="result"/> was injected in place of the call.</param>
    public void AfterPresent(int image, VkResult result, bool injected) =>
        _armed[image] = !injected && result is VkResult.Success or VkResult.SuboptimalKHR or VkResult.ErrorOutOfDateKHR
            or VkResult.ErrorSurfaceLostKHR or VkResult.ErrorFullScreenExclusiveModeLostEXT;

    /// <summary>Waits for every armed fence in one bounded call; no call when none is armed.</summary>
    /// <param name="device">The swapchain's device.</param>
    /// <param name="gate">The device's gate.</param>
    /// <param name="timeout">The wait for all of them in nanoseconds, as BRUTAL's <c>nint</c> for <c>uint64_t</c>.</param>
    /// <returns>Success once every present the fences were enqueued with has finished; or the wait's error.</returns>
    public Result WaitAll(Device device, VulkanCallGate gate, nint timeout)
    {
        int count = 0;
        for (int i = 0; i < _fences.Length; i++)
        {
            if (_armed[i])
            {
                _waitList[count++] = _fences[i];
            }
        }

        if (count == 0)
        {
            return Result.Success;
        }

        // Sound: the list is pinned for the call, which reads its first count handles.
        fixed (VkFence* fences = _waitList)
        {
            return Wait(device, gate, fences, count, timeout);
        }
    }

    /// <summary>Destroys every created fence once, newest first.</summary>
    /// <param name="device">The swapchain's device.</param>
    /// <param name="destroys">A test's counters of destroyed objects, or <see langword="null"/>.</param>
    /// <remarks>Only once no present can still signal them: <see cref="WaitAll"/> succeeded, or the device was idle or lost.</remarks>
    public void Destroy(Device device, VulkanSwapchain.DestroyCounts? destroys)
    {
        for (int i = _fences.Length - 1; i >= 0; i--)
        {
            if (!_fences[i].IsNull())
            {
                device.PInvoke.vkDestroyFence.Invoke(device.Handle, _fences[i], null);
                destroys?.OnFence();
                _fences[i] = default;
            }
        }
    }

    // One bounded wait for all the given fences. A failure other than a reported loss marks a timeout loss.
    private static Result Wait(Device device, VulkanCallGate gate, VkFence* fences, int count, nint timeout)
    {
        VkResult result = gate.TryTakeFault(VulkanCall.WaitForPresentFence, out VkResult injected) ? injected
            : device.PInvoke.vkWaitForFences.Invoke(device.Handle, count, fences, true, timeout);
        Result waited = gate.Check(result, VulkanCall.WaitForPresentFence);
        if (waited.IsFailure && result != VkResult.ErrorDeviceLost)
        {
            gate.MarkLost(timedOut: true);
        }

        return waited;
    }
}
