using System.Globalization;

namespace BrutalEngine.Graphics.Backend;

/// <summary>The outcome of a backend's swapchain image acquire.</summary>
/// <param name="Status">What the acquire reported.</param>
/// <param name="ImageIndex">
/// The acquired image's index when <paramref name="Status"/> is <see cref="SwapchainStatus.Success"/> or
/// <see cref="SwapchainStatus.Suboptimal"/>; -1 otherwise.
/// </param>
/// <param name="NativeCode">
/// The native status of the acquire (for Vulkan, the <c>VkResult</c>), 0 on success; the front end puts it in the
/// device-lost event for <see cref="SwapchainStatus.DeviceLost"/>.
/// </param>
/// <remarks>
/// <para>
/// A plain unmanaged value, returned without allocating. Equality compares every field. Immutable and safe to copy between
/// threads. No member allocates, except <see cref="ToString"/>.
/// </para>
/// </remarks>
public readonly record struct BackendAcquire(SwapchainStatus Status, int ImageIndex, int NativeCode)
{
    /// <summary>
    /// Returns every field in the record's usual form, for example
    /// <c>BackendAcquire { Status = Success, ImageIndex = 1, NativeCode = 0 }</c>.
    /// </summary>
    /// <returns>A diagnostic string, formatted with the invariant culture whatever the current culture is. Allocates.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture, $"BackendAcquire {{ Status = {Status}, ImageIndex = {ImageIndex}, NativeCode = {NativeCode} }}");
}
