using System;
using BrutalEngine.Core.Logging;

namespace BrutalEngine.Graphics.Vulkan;

/// <summary>
/// Settings for creating the Vulkan backend with <c>VulkanBackend.Create</c> (docs/design/graphics-vulkan.md, "The
/// backend's public surface").
/// </summary>
/// <remarks>
/// The record only describes the settings; <c>VulkanBackend.Create</c> validates them and throws
/// <see cref="ArgumentException"/> for a missing or relative <see cref="AppDirectory"/>, a null <see cref="Logger"/>,
/// a relative <see cref="LoaderPath"/> or an allowance without a reason. Equality compares every property:
/// <see cref="Logger"/> by reference and <see cref="ValidationAllowList"/> by memory region (the same array and range),
/// not by its contents. An immutable record: safe to share between threads. Creating one allocates; reading it doesn't.
/// </remarks>
public sealed record VulkanBackendOptions
{
    /// <summary>
    /// Gets the app's directory, fully qualified: where bundled natives (the macOS loader and MoltenVK manifest) would be.
    /// </summary>
    /// <value>Usually <see cref="AppContext.BaseDirectory"/>. Only macOS (unverified, no CI) looks in it.</value>
    public required string AppDirectory { get; init; }

    /// <summary>Gets the logger the backend writes its loader decision to, in the <c>Graphics.Vulkan</c> category.</summary>
    /// <value>Not owned by the backend: the host disposes it, after the backend's last use.</value>
    public required Logger Logger { get; init; }

    /// <summary>
    /// Gets the fully qualified path of a Vulkan loader to use instead of the system's (or, on macOS, the bundled one),
    /// or <see langword="null"/> (the default) to let the backend choose.
    /// </summary>
    /// <value>
    /// Preloaded by absolute path on every OS and checked: when BRUTAL binds a different loader, creation fails rather
    /// than falling back. Hosts usually take it from a <c>--vulkan-loader</c> command-line option.
    /// </value>
    public string? LoaderPath { get; init; }

    /// <summary>
    /// Gets the validation messages the backend's devices tolerate, each with its reason. Empty by default: the backend
    /// ships no default list.
    /// </summary>
    /// <value>Copied when the backend is created, so changing the underlying array afterwards has no effect on it.</value>
    public ReadOnlyMemory<ValidationAllowance> ValidationAllowList { get; init; }
}
