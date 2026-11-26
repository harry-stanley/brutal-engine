using System;
using System.Runtime.InteropServices;
using Brutal.SdlApi;
using BrutalEngine.Core;

namespace BrutalEngine.Platform.Sdl3;

/// <summary>
/// The Vulkan surface source of one SDL window (docs/design/platform.md section 4): SDL's required instance
/// extensions, copied once into storage it owns, and surface creation through the raw, status-returning SDL call.
/// </summary>
/// <remarks>
/// <para>
/// The window that owns it creates it right after creating an SDL window with <c>SDL_WINDOW_VULKAN</c>, and the
/// source lives exactly as long as that window: once <see cref="IOwner.IsDisposed"/> is true, every member throws
/// <see cref="ObjectDisposedException"/>.
/// </para>
/// <para>
/// <b>Threads:</b> the extension getters run on any thread; they read immutable arrays and the owner's disposed
/// flag. <see cref="CreateVulkanSurface"/> is main thread only, checked with the owner's <see cref="ThreadAffinity"/>.
/// </para>
/// <para>
/// <b>Allocation:</b> <see cref="Create"/> allocates the source and its two arrays (or the error); the getters
/// allocate nothing; <see cref="CreateVulkanSurface"/> allocates only on failure (the error message) or misuse.
/// </para>
/// </remarks>
internal sealed class SdlVulkanSurfaceSource : IVulkanSurfaceSource
{
    private const string CreateSurfaceName = "IVulkanSurfaceSource.CreateVulkanSurface";

    private readonly SdlWindow _window;
    private readonly ThreadAffinity _mainThread;
    private readonly IOwner _owner;

    // Every name back to back, without NUL terminators. Name i is _names[_offsets[i].._offsets[i + 1]].
    private readonly byte[] _names;
    private readonly int[] _offsets;

    private SdlVulkanSurfaceSource(SdlWindow window, ThreadAffinity mainThread, IOwner owner, byte[] names, int[] offsets)
    {
        _window = window;
        _mainThread = mainThread;
        _owner = owner;
        _names = names;
        _offsets = offsets;
    }

    /// <summary>The window that owns a surface source, which tells it when the window has been disposed.</summary>
    /// <remarks>
    /// An interface rather than a delegate, so the check allocates no closure. The source reads
    /// <see cref="IsDisposed"/> from any thread, so the implementation publishes it with a volatile write.
    /// </remarks>
    internal interface IOwner
    {
        /// <summary>Whether the window has been disposed: once true, it stays true.</summary>
        public bool IsDisposed { get; }
    }

    /// <inheritdoc/>
    public int RequiredInstanceExtensionCount
    {
        get
        {
            ThrowIfDisposed();
            return _offsets.Length - 1;
        }
    }

    /// <summary>Creates the source for an SDL window and copies SDL's required instance extensions.</summary>
    /// <param name="window">The SDL window, created with <c>SDL_WINDOW_VULKAN</c> and owned by <paramref name="owner"/>.</param>
    /// <param name="mainThread">The platform's main thread, the only one <see cref="CreateVulkanSurface"/> runs on.</param>
    /// <param name="owner">The window object, asked whether it has been disposed.</param>
    /// <returns>
    /// The source, or <see cref="ErrorKind.Native"/> with SDL's text if <c>SDL_Vulkan_GetInstanceExtensions</c>
    /// returns <c>NULL</c>, which SDL documents as failure (SDL 3.2's drivers return static lists for Vulkan windows).
    /// </returns>
    /// <remarks>
    /// Main thread (the window's creation). Calls <c>SDL_Vulkan_GetInstanceExtensions</c> once and never again: SDL's
    /// list is the same for every window of one process. SDL owns the returned array; the names are copied, not freed.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="owner"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="window"/> is a null handle, or a window created without <c>SDL_WINDOW_VULKAN</c>.
    /// </exception>
    public static unsafe Result<SdlVulkanSurfaceSource> Create(SdlWindow window, ThreadAffinity mainThread, IOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (window.IsNull())
        {
            throw new ArgumentException("The SDL window handle is null.", nameof(window));
        }

        // SDL_Vulkan_GetInstanceExtensions takes no window and checks nothing: on a driver without Vulkan support (dummy)
        // it calls a null function pointer. A window created with SDL_WINDOW_VULKAN proves the driver supports Vulkan
        // and has loaded the Vulkan library (SDL_CreateWindow fails otherwise), so it is required.
        if ((Sdl.PInvoke.GetWindowFlags(window) & SdlWindowFlags.Vulkan) == 0)
        {
            throw new ArgumentException("The SDL window wasn't created with SDL_WINDOW_VULKAN.", nameof(window));
        }

        uint count;

        // Sound: count is a stack local that outlives the call. On success SDL returns an array of count pointers to
        // NUL-terminated ASCII strings, which SDL owns and keeps valid while its Vulkan library is loaded, so reading
        // them below, before returning, is valid.
        byte** names = Sdl.PInvoke.Vulkan_GetInstanceExtensions(&count);
        if (names is null)
        {
            return SdlCheck.Error("SDL_Vulkan_GetInstanceExtensions");
        }

        int[] offsets = new int[checked((int)count + 1)];
        for (int i = 0; i < offsets.Length - 1; i++)
        {
            offsets[i + 1] = checked(offsets[i] + MemoryMarshal.CreateReadOnlySpanFromNullTerminated(names[i]).Length);
        }

        byte[] copy = new byte[offsets[^1]];
        for (int i = 0; i < offsets.Length - 1; i++)
        {
            MemoryMarshal.CreateReadOnlySpanFromNullTerminated(names[i]).CopyTo(copy.AsSpan(offsets[i]));
        }

        return new SdlVulkanSurfaceSource(window, mainThread, owner, copy, offsets);
    }

    /// <inheritdoc/>
    public ReadOnlySpan<byte> GetRequiredInstanceExtension(int index)
    {
        ThrowIfDisposed();
        int[] offsets = _offsets;
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, offsets.Length - 1);
        return _names.AsSpan(offsets[index], offsets[index + 1] - offsets[index]);
    }

    /// <inheritdoc/>
    public unsafe Result<nint> CreateVulkanSurface(nint instance)
    {
        _mainThread.ThrowIfNotCurrentThread(CreateSurfaceName);
        ThrowIfDisposed();
        if (instance == 0)
        {
            throw new ArgumentException("The VkInstance handle is 0.", nameof(instance));
        }

        nint surface = 0;

        // Sound: the window is alive (its owner isn't disposed, checked above on the main thread, the only thread that
        // disposes it), and surface is a stack local that SDL writes before returning. No allocation callbacks (0).
        Result status = SdlCheck.Check(Sdl.PInvoke.Vulkan_CreateSurface(_window, instance, 0, &surface), "SDL_Vulkan_CreateSurface");
        return status.IsFailure ? status.Error : surface;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_owner.IsDisposed, this);
}
