using System.Globalization;

namespace BrutalEngine.Graphics.Backend;

/// <summary>What the front end asks a backend for when it creates or recreates a swapchain.</summary>
/// <param name="Surface">The window's surface id, from the backend's surface creation.</param>
/// <param name="Source">
/// The window the surface belongs to, with its current pixel size and minimised state, refreshed by the front end just
/// before the call.
/// </param>
/// <param name="Options">The requested present mode, format kind and debug name.</param>
/// <param name="OldSwapchain">
/// The swapchain being replaced, or 0 for a first creation. When non-zero, <paramref name="Surface"/> is that swapchain's
/// surface and the backend retires the old swapchain into the new one; the front end destroys the old id afterwards
/// whether the creation succeeded or failed.
/// </param>
/// <remarks>
/// <para>
/// The front end validates the request before the call: the ids are live, the options are valid, and the window isn't
/// minimised. The request holds the source without owning it.
/// </para>
/// <para>
/// Equality compares every field (<see cref="Source"/> by reference). Immutable; the backend reads it on the device's owner
/// thread. No member allocates, except <see cref="ToString"/>.
/// </para>
/// </remarks>
public readonly record struct BackendSwapchainRequest(ulong Surface, WindowSurfaceSource Source, SwapchainOptions Options, ulong OldSwapchain)
{
    /// <summary>
    /// Returns every field in the record's usual form, for example
    /// <c>BackendSwapchainRequest { Surface = 7, Source = BrutalEngine.Graphics.Backend.WindowSurfaceSource, Options = SwapchainOptions { ... }, OldSwapchain = 0 }</c>.
    /// </summary>
    /// <returns>
    /// A diagnostic string, formatted with the invariant culture whatever the current culture is. <see cref="Source"/>
    /// prints its type name, or nothing when <see langword="null"/>. Allocates.
    /// </returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"BackendSwapchainRequest {{ Surface = {Surface}, Source = {Source}, Options = {Options}, OldSwapchain = {OldSwapchain} }}");
}
