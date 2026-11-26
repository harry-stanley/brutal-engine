using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>Settings for creating a window's swapchain: its present mode, format kind and debug name.</summary>
/// <remarks>
/// <para>
/// The record only describes the swapchain; the device checks it when it creates one (docs/design/graphics.md
/// section 3). A present mode the surface doesn't offer isn't an error: the swapchain falls back to
/// <see cref="PresentMode.Fifo"/> and reports the mode in use in <see cref="SwapchainInfo"/>.
/// </para>
/// <para>
/// Equality compares every property; the debug name compares ordinally. An immutable record: the device copies what it
/// needs, so a later <c>with</c> copy has no effect on a created swapchain. Safe to share between threads. Creating one
/// allocates; no other member does, except <see cref="ToString"/>.
/// </para>
/// </remarks>
public sealed record SwapchainOptions
{
    /// <summary>The present mode to ask for. Defaults to <see cref="PresentMode.Fifo"/> (vertical sync).</summary>
    public PresentMode PresentMode { get; init; } = PresentMode.Fifo;

    /// <summary>The kind of format the images use. Defaults to <see cref="SwapchainFormat.Srgb"/>.</summary>
    public SwapchainFormat Format { get; init; } = SwapchainFormat.Srgb;

    /// <summary>
    /// An optional name for debuggers, validation messages, errors and the leak report (docs/design/graphics.md
    /// section 13); <see langword="null"/> (the default) for none.
    /// </summary>
    public string? DebugName { get; init; }

    /// <summary>
    /// Returns every property, for example <c>SwapchainOptions { PresentMode = Mailbox, Format = Srgb, DebugName = main }</c>.
    /// </summary>
    /// <returns>A diagnostic string, formatted with the invariant culture whatever the current culture is. Allocates.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"SwapchainOptions {{ PresentMode = {PresentMode}, Format = {Format}, DebugName = {DebugName} }}");
}
