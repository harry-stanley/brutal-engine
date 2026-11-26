using System.Globalization;

namespace BrutalEngine.Graphics.Backend;

/// <summary>A swapchain a backend created: its id and what it really is.</summary>
/// <param name="Id">The swapchain's id: non-zero, chosen by the backend and opaque to the front end.</param>
/// <param name="Info">The swapchain's size, format, present mode, image count and copy support, as created.</param>
/// <remarks>
/// <para>
/// The backend owns the swapchain and its images until the front end destroys it. The images' texture and view ids come
/// from the backend's swapchain-image query after each creation, belong to the swapchain, and are never destroyed on
/// their own.
/// </para>
/// <para>
/// Equality compares both fields. Immutable and safe to copy between threads. No member allocates, except
/// <see cref="ToString"/>.
/// </para>
/// </remarks>
public readonly record struct BackendSwapchain(ulong Id, SwapchainInfo Info)
{
    /// <summary>
    /// Returns both fields in the record's usual form, for example
    /// <c>BackendSwapchain { Id = 3, Info = SwapchainInfo { Width = 1920, ... } }</c>.
    /// </summary>
    /// <returns>A diagnostic string, formatted with the invariant culture whatever the current culture is. Allocates.</returns>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"BackendSwapchain {{ Id = {Id}, Info = {Info} }}");
}
