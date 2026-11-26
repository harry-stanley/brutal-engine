using System;
using Brutal.VulkanApi;

namespace BrutalEngine.Graphics.Vulkan;

/// <summary>
/// The backend's fault seam (docs/design/graphics-vulkan.md section 12): makes a chosen occurrence of a call return a
/// chosen result instead of reaching the driver, so tests can prove the backend's own detection.
/// </summary>
/// <remarks>
/// <para>
/// Compiled into release builds and internal; only <c>BrutalEngine.Graphics.Vulkan.Tests</c> sets one on a gate. In
/// production <see cref="VulkanCallGate.Faults"/> is <see langword="null"/>, which costs the gate one null check per
/// call. An injected result replaces the native call, so no object is created or submitted behind it.
/// </para>
/// <para>
/// At most one fault is armed per call at a time; it fires once and is then disarmed. Occurrences are counted from
/// <see cref="FailNth"/>, so arming never depends on how often the call ran before. Not thread-safe: used on the
/// device's owner thread, like the gate. <see cref="TryTake"/> allocates nothing.
/// </para>
/// </remarks>
internal sealed class VulkanFaultInjector
{
    // Per call, indexed by member (VulkanCall is contiguous from 0): calls left until the armed fault fires, 0 when
    // none is armed, and the result it returns.
    private readonly int[] _remaining;
    private readonly VkResult[] _results;

    /// <summary>Creates an injector with no armed faults.</summary>
    public VulkanFaultInjector()
    {
        int calls = Enum.GetValues<VulkanCall>().Length;
        _remaining = new int[calls];
        _results = new VkResult[calls];
    }

    /// <summary>Arms a fault: the <paramref name="occurrence"/>-th call of <paramref name="call"/> from now returns <paramref name="result"/>.</summary>
    /// <param name="call">The call to fail.</param>
    /// <param name="occurrence">Which call fails, 1-based: 1 is the next call.</param>
    /// <param name="result">The result to return instead of calling the driver; anything but <c>SUCCESS</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="call"/> isn't a <see cref="VulkanCall"/> member, or <paramref name="occurrence"/> is less than 1.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="result"/> is <c>SUCCESS</c>: a success would skip the native call.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="call"/> already has an armed fault that hasn't fired.</exception>
    public void FailNth(VulkanCall call, int occurrence, VkResult result)
    {
        int index = IndexOf(call);
        ArgumentOutOfRangeException.ThrowIfLessThan(occurrence, 1);
        if (result == VkResult.Success)
        {
            throw new ArgumentException("An injected result must be a failure; VK_SUCCESS would skip the native call.", nameof(result));
        }

        if (_remaining[index] != 0)
        {
            throw new InvalidOperationException($"{call} already has an armed fault that hasn't fired.");
        }

        _remaining[index] = occurrence;
        _results[index] = result;
    }

    /// <summary>Counts one call of <paramref name="call"/> and reports whether its armed fault fires now.</summary>
    /// <param name="call">The call about to be made.</param>
    /// <param name="result">The injected result when this returns <see langword="true"/>; <c>SUCCESS</c> otherwise.</param>
    /// <returns><see langword="true"/> if this is the armed occurrence; the fault is then disarmed.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="call"/> isn't a <see cref="VulkanCall"/> member.</exception>
    public bool TryTake(VulkanCall call, out VkResult result)
    {
        int index = IndexOf(call);
        if (_remaining[index] == 0 || --_remaining[index] != 0)
        {
            result = VkResult.Success;
            return false;
        }

        result = _results[index];
        return true;
    }

    private int IndexOf(VulkanCall call)
    {
        if ((uint)call >= (uint)_remaining.Length)
        {
            ThrowUnknownCall(call);
        }

        return (int)call;
    }

    private static void ThrowUnknownCall(VulkanCall call) =>
        throw new ArgumentOutOfRangeException(nameof(call), call, "Not a VulkanCall member.");
}
