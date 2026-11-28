using System;
using Brutal.VulkanApi;
using BrutalEngine.Core;

namespace BrutalEngine.Graphics.Vulkan;

/// <summary>
/// The per-device half of the backend's status checking: every result-returning Vulkan or VMA call the device makes
/// passes through one gate, which turns its result into a <see cref="Result"/> and detects device loss in one place.
/// </summary>
/// <remarks>
/// <para>
/// A call site asks <see cref="TryTakeFault"/> first and, when no fault fires, makes the native call and passes its
/// result to <see cref="Check"/>. <c>VK_ERROR_DEVICE_LOST</c> from any call marks the device lost, and it stays lost.
/// A wait that runs out of <c>GpuTimeout</c> marks it lost as a timeout instead (<see cref="MarkLost"/>), which the gate
/// remembers, so later calls report <see cref="ErrorKind.Timeout"/> (<see cref="LostError"/>).
/// </para>
/// <para>
/// The rules every call site follows are in
/// <see href="https://github.com/harry-stanley/brutal-engine/blob/main/docs/design/graphics-vulkan.md#13-status-checking">docs/design/graphics-vulkan.md, section 13, "Status checking"</see>,
/// and review enforces them:
/// </para>
/// <list type="number">
/// <item><description>Raw function tables for every result-returning call after creation, each with its own
/// <see cref="VulkanCall"/> member, then <see cref="Check"/>.</description></item>
/// <item><description>Only three BRUTAL wrappers, each at one creation-time call site that catches
/// <see cref="VkResultError"/> and converts it with <see cref="VkCheck.FromException"/>. The only other caught
/// exception is the <see cref="TypeInitializationException"/> of a missing loader, in <c>VulkanBackend.Create</c>. No
/// exception is ever caught on the frame path.</description></item>
/// <item><description>Void commands also go through the raw table.</description></item>
/// <item><description>Extension entry points only when their extension is enabled.</description></item>
/// <item><description>VMA: only the generated, result-returning calls, through this gate.</description></item>
/// <item><description>Submit and present through the raw table, on the owner thread, skipping BRUTAL's queue
/// lock.</description></item>
/// <item><description><c>uint64_t</c> timeouts passed as <c>unchecked((nint)ns)</c>.</description></item>
/// <item><description>A discarded <see cref="Result"/> or <see cref="VkResult"/> fails the build
/// (BE1001).</description></item>
/// </list>
/// <para>
/// Not thread-safe: the device's owner thread calls it, never re-entrantly. <see cref="Check"/> and
/// <see cref="TryTakeFault"/> and <see cref="MarkLost"/> allocate nothing.
/// </para>
/// </remarks>
internal sealed class VulkanCallGate
{
    private bool _isLost;
    private bool _timedOut;

    /// <summary><see langword="true"/> once any call returned <c>VK_ERROR_DEVICE_LOST</c> or <see cref="MarkLost"/> ran; never reset.</summary>
    public bool IsLost => _isLost;

    /// <summary>
    /// <see langword="true"/> when the loss came from a timeout (<see cref="MarkLost"/>): a fence or present-fence
    /// wait that ran out, so the GPU may be hung and still using what it was given. Never reset; a loss already reported
    /// as <c>VK_ERROR_DEVICE_LOST</c> stays a reported loss.
    /// </summary>
    public bool LostTimedOut => _timedOut;

    /// <summary>
    /// Gets the error that calls on a lost device return without calling the driver (docs/design/graphics-vulkan.md
    /// section 12): <see cref="ErrorKind.Timeout"/> ("device is lost after a GPU timeout", <c>VK_TIMEOUT</c>) after a
    /// timeout loss, otherwise <see cref="ErrorKind.Unavailable"/> ("device is lost", <c>VK_ERROR_DEVICE_LOST</c>).
    /// </summary>
    public EngineError LostError => _timedOut
        ? new EngineError(ErrorKind.Timeout, "device is lost after a GPU timeout", (int)VkResult.Timeout)
        : new EngineError(ErrorKind.Unavailable, "device is lost", (int)VkResult.ErrorDeviceLost);

    /// <summary>Marks the device lost without a failed result: a wait that ran out of <c>GpuTimeout</c>.</summary>
    /// <param name="timedOut">Whether the loss is a timeout (<see cref="LostTimedOut"/>).</param>
    /// <remarks>The first loss decides the kind: on a device already lost this changes nothing.</remarks>
    public void MarkLost(bool timedOut)
    {
        if (!_isLost)
        {
            _isLost = true;
            _timedOut = timedOut;
        }
    }

    /// <summary>The fault seam; <see langword="null"/> in production, set only by the backend's tests.</summary>
    public VulkanFaultInjector? Faults { get; set; }

    /// <summary>Asks the fault seam whether this call is replaced by an injected result.</summary>
    /// <param name="call">The call about to be made.</param>
    /// <param name="result">The injected result when this returns <see langword="true"/>; <c>SUCCESS</c> otherwise.</param>
    /// <returns><see langword="true"/> if the caller must skip the native call and check <paramref name="result"/> instead.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="call"/> isn't a <see cref="VulkanCall"/> member.</exception>
    public bool TryTakeFault(VulkanCall call, out VkResult result)
    {
        VulkanFaultInjector? faults = Faults;
        if (faults is null)
        {
            result = VkResult.Success;
            return false;
        }

        return faults.TryTake(call, out result);
    }

    /// <summary>Checks a call's result and marks the device lost on <c>VK_ERROR_DEVICE_LOST</c>.</summary>
    /// <param name="result">The call's result, native or injected.</param>
    /// <param name="call">The call that returned it.</param>
    /// <returns>
    /// Success for <c>SUCCESS</c> only. Any other value is a failure carrying <see cref="VkCheck.Error"/>'s error:
    /// a caller that accepts another success code (<c>INCOMPLETE</c>, <c>NOT_READY</c>) tests for it first.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="result"/> is a failure and <paramref name="call"/> isn't a <see cref="VulkanCall"/> member (a success
    /// returns at once, without looking at the call).
    /// </exception>
    public Result Check(VkResult result, VulkanCall call)
    {
        if (result == VkResult.Success)
        {
            return Result.Success;
        }

        // Built before the flag is set, so an undefined call throws without marking the device lost.
        EngineError error = VkCheck.Error(result, call);
        if (result == VkResult.ErrorDeviceLost)
        {
            _isLost = true;
        }

        return Result.Failure(error);
    }
}
