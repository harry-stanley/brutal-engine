using System.Globalization;

namespace BrutalEngine.Graphics.Backend;

/// <summary>The outcome of a backend's swapchain image present.</summary>
/// <param name="Status">What the present reported.</param>
/// <param name="NativeCode">
/// The native status of the present (for Vulkan, the <c>VkResult</c>), 0 on success; the front end puts it in the
/// device-lost event for <see cref="SwapchainStatus.DeviceLost"/>.
/// </param>
/// <remarks>
/// <para>
/// A plain unmanaged value, returned without allocating. Equality compares both fields. Immutable and safe to copy between
/// threads. No member allocates, except <see cref="ToString"/>.
/// </para>
/// </remarks>
public readonly record struct BackendPresent(SwapchainStatus Status, int NativeCode)
{
    /// <summary>Returns both fields in the record's usual form, for example <c>BackendPresent { Status = OutOfDate, NativeCode = -1000001004 }</c>.</summary>
    /// <returns>A diagnostic string, formatted with the invariant culture whatever the current culture is. Allocates.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture, $"BackendPresent {{ Status = {Status}, NativeCode = {NativeCode} }}");
}
