using System;
using BrutalEngine.Core;

namespace BrutalEngine.Platform;

/// <summary>
/// The Vulkan side of a window: the instance extensions it needs and the call that creates its surface. Backend-neutral:
/// only <see langword="nint"/> handles and spans, so the render interface can use it without referencing a binding.
/// </summary>
/// <remarks>
/// <para>
/// A window created with a Vulkan surface (<c>WindowSurface.Vulkan</c>) hands one out through
/// <see cref="IWindow.VulkanSurface"/>, and it exists exactly as long as that window: the same object until the window
/// is disposed, after which every member throws <see cref="ObjectDisposedException"/>.
/// </para>
/// <para>
/// <b>Threads:</b> <see cref="RequiredInstanceExtensionCount"/> and <see cref="GetRequiredInstanceExtension"/> run on
/// any thread. <see cref="CreateVulkanSurface"/> is main thread only.
/// </para>
/// <para>
/// <b>Ownership:</b> the caller owns each surface it creates and destroys it with <c>vkDestroySurfaceKHR</c> from the
/// instance that created it: after its swapchain, before the window is disposed and before the instance is destroyed.
/// At most one surface is alive per window. The window can't observe <c>vkDestroySurfaceKHR</c>, so it can't check
/// either rule; the host's reverse-order shutdown (graphics before platform) keeps them.
/// </para>
/// <para>See docs/design/platform.md, section 4.</para>
/// </remarks>
public interface IVulkanSurfaceSource
{
    /// <summary>The number of instance extensions the Vulkan instance must enable to create surfaces for this window.</summary>
    /// <remarks>
    /// The same for every window of one platform, so a graphics backend reads it from its first window before creating its
    /// instance. Any thread. Allocates nothing.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    public int RequiredInstanceExtensionCount { get; }

    /// <summary>Gets the name of one required instance extension, for example <c>VK_KHR_surface</c>.</summary>
    /// <param name="index">The extension's index, from <c>0</c> to <see cref="RequiredInstanceExtensionCount"/> minus 1.</param>
    /// <returns>
    /// The name as UTF-8 (the names are ASCII), without a NUL terminator. The span points into storage the window owns and
    /// stays valid while the window is alive.
    /// </returns>
    /// <remarks>Any thread. Allocates nothing.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative, or not below <see cref="RequiredInstanceExtensionCount"/>.</exception>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    public ReadOnlySpan<byte> GetRequiredInstanceExtension(int index);

    /// <summary>Creates a Vulkan surface (<c>VkSurfaceKHR</c>) for this window.</summary>
    /// <param name="instance">
    /// The <c>VkInstance</c>, created with every extension from <see cref="GetRequiredInstanceExtension"/> enabled.
    /// </param>
    /// <returns>
    /// The <c>VkSurfaceKHR</c> handle, owned by the caller (see the type's remarks). A failure is
    /// <see cref="ErrorKind.Native"/> with the backend's error text.
    /// </returns>
    /// <remarks>
    /// Main thread only. The handle is a non-dispatchable 64-bit Vulkan handle, which fits in <see langword="nint"/>
    /// because every supported runtime is 64-bit. Allocates only on failure (the error message).
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="instance"/> is <c>0</c>.</exception>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public Result<nint> CreateVulkanSurface(nint instance);
}
