using System;
using System.Collections.Generic;
using System.IO;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Vulkan.Adapters;
using BrutalEngine.Graphics.Vulkan.Loader;

namespace BrutalEngine.Graphics.Vulkan;

/// <summary>
/// The Vulkan 1.3 backend of the render interface. Creating it is the host's first graphics step: it settles which
/// Vulkan loader the process uses (docs/design/graphics-vulkan.md, "The backend's public surface").
/// </summary>
/// <remarks>
/// <para>
/// Create it with <see cref="Create(VulkanBackendOptions)"/> on the main thread, before the platform, any window or any other thread starts,
/// then give <see cref="LoaderPath"/> to the platform unchanged (Platform.Sdl3's <c>SdlPlatformOptions.VulkanLibraryPath</c>),
/// so SDL loads the same loader. The host guarantees that order; the backend can't check it.
/// </para>
/// <para>
/// Immutable, and holds no native object: the loader it chose belongs to the process and stays loaded until the
/// process exits, so there is nothing to dispose. <see cref="Name"/> and <see cref="LoaderPath"/> may be read from any
/// thread and allocate nothing; <see cref="EnumerateAdapters"/> creates and destroys its own native objects within the
/// call.
/// </para>
/// </remarks>
public sealed class VulkanBackend
{
    /// <summary>The log category the backend writes its loader decision to.</summary>
    internal const string LogCategoryName = "Graphics.Vulkan";

    private readonly ValidationAllowance[] _validationAllowList;

    private VulkanBackend(string? loaderPath, ValidationAllowance[] validationAllowList)
    {
        LoaderPath = loaderPath;
        _validationAllowList = validationAllowList;
    }

    /// <summary>Gets the backend's name.</summary>
    /// <value>Always <c>"Vulkan"</c>. Doesn't allocate.</value>
    public string Name { get; } = "Vulkan";

    /// <summary>
    /// Gets the absolute path of the Vulkan loader the backend preloaded and BRUTAL bound, or <see langword="null"/>
    /// when BRUTAL's own search found the system loader.
    /// </summary>
    /// <value>
    /// <see cref="VulkanBackendOptions.LoaderPath"/> when one was given; on macOS (unverified, no CI) the loader
    /// bundled in <see cref="VulkanBackendOptions.AppDirectory"/> when there is one; otherwise <see langword="null"/>.
    /// The host passes it unchanged as the library the platform's SDL loads. Doesn't allocate.
    /// </value>
    public string? LoaderPath { get; }

    /// <summary>Gets the backend's own copy of <see cref="VulkanBackendOptions.ValidationAllowList"/>.</summary>
    internal ReadOnlySpan<ValidationAllowance> ValidationAllowList => _validationAllowList;

    /// <summary>Settles the Vulkan loader for the process and creates the backend.</summary>
    /// <param name="options">The settings. The backend copies the allow-list and keeps no reference to the record.</param>
    /// <returns>
    /// The backend, after its loader decision was logged once at <see cref="LogLevel.Information"/> in the
    /// <c>Graphics.Vulkan</c> category ("Vulkan loader: &lt;path&gt; (explicit|bundled)", or "Vulkan loader: BRUTAL's
    /// search (system)", plus the driver manifests on macOS). Or a failure, with nothing logged:
    /// <see cref="ErrorKind.Unavailable"/> when no loader was found ("Vulkan loader missing: " and BRUTAL's message),
    /// when the explicit or bundled loader couldn't be loaded, or when BRUTAL had already bound a different loader than
    /// the chosen one (a second <see cref="Create(VulkanBackendOptions)"/> naming another loader); <see cref="ErrorKind.Native"/> when setting
    /// the driver variable failed (macOS only).
    /// </returns>
    /// <remarks>
    /// <para>
    /// Main thread only, before the platform, any window or any other thread starts: on macOS it may set
    /// <c>VK_DRIVER_FILES</c>, and the loader reads that variable when it first enumerates drivers. The first call in a
    /// process binds BRUTAL's Vulkan class to the loader for the process lifetime. A later call with the same choice
    /// succeeds with the same <see cref="LoaderPath"/> and logs the decision again.
    /// </para>
    /// <para>Allocates the backend, its allow-list copy, the log entry and, on failure, the error's message.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <see cref="VulkanBackendOptions.AppDirectory"/> is <see langword="null"/> or not fully qualified;
    /// <see cref="VulkanBackendOptions.Logger"/> is <see langword="null"/>; <see cref="VulkanBackendOptions.LoaderPath"/>
    /// is set and not fully qualified; or an entry of <see cref="VulkanBackendOptions.ValidationAllowList"/> has a
    /// <see langword="null"/>, empty or white-space <see cref="ValidationAllowance.Reason"/>.
    /// </exception>
    /// <exception cref="ObjectDisposedException"><see cref="VulkanBackendOptions.Logger"/> is disposed.</exception>
    public static Result<VulkanBackend> Create(VulkanBackendOptions options) => Create(options, default(ProcessLoaderHost));

    /// <summary>
    /// <see cref="Create(VulkanBackendOptions)"/> over a given process, so tests can script the loader's failures.
    /// </summary>
    /// <typeparam name="THost">The process abstraction; the real one is a struct, so nothing is boxed.</typeparam>
    /// <param name="options">The settings.</param>
    /// <param name="host">The process abstraction.</param>
    /// <returns>As <see cref="Create(VulkanBackendOptions)"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="host"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">As <see cref="Create(VulkanBackendOptions)"/>.</exception>
    /// <exception cref="ObjectDisposedException">As <see cref="Create(VulkanBackendOptions)"/>.</exception>
    internal static Result<VulkanBackend> Create<THost>(VulkanBackendOptions options, THost host)
        where THost : IVulkanLoaderHost
    {
        ArgumentNullException.ThrowIfNull(options);

        // The type test keeps a struct host from being boxed for the null check in unoptimised code.
        if (!typeof(THost).IsValueType && host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        Validate(options);

        // Before any effect on the process, so a disposed logger fails without touching the loader.
        LogCategory log = options.Logger.CreateCategory(LogCategoryName);
        ValidationAllowance[] allowList = options.ValidationAllowList.ToArray();

        Result<LoaderDecision> configured = LoaderSetup.Configure(options.AppDirectory, options.LoaderPath, host);
        if (!configured.TryGetValue(out LoaderDecision decision))
        {
            return configured.Error;
        }

        string where = decision.PreloadedPath ?? "BRUTAL's search";
        if (decision.DriverFiles is { } driverFiles)
        {
            log.Information($"Vulkan loader: {where} ({OriginName(decision.Origin)}), driver files: {driverFiles}");
        }
        else
        {
            log.Information($"Vulkan loader: {where} ({OriginName(decision.Origin)})");
        }

        return new VulkanBackend(decision.PreloadedPath, allowList);
    }

    /// <summary>
    /// Lists every Vulkan physical device, each with the reasons it can't serve <paramref name="request"/> (empty when it
    /// can), as <c>IGraphicsBackend.EnumerateAdapters</c> describes (docs/design/graphics-vulkan.md section 3).
    /// </summary>
    /// <param name="request">
    /// The device request. Its <see cref="BackendDeviceRequest.ApplicationName"/>, <see cref="BackendDeviceRequest.Validation"/>
    /// and <see cref="BackendDeviceRequest.CompatibleSurface"/> shape the temporary instance; its
    /// <see cref="BackendDeviceRequest.RequiredFeatures"/> and surface decide the rejection reasons.
    /// </param>
    /// <returns>
    /// The adapters in BRUTAL's enumeration order, <see cref="GraphicsAdapterInfo.Index"/> equal to the position, each
    /// with its name, type, driver (a fixed name such as <c>NvidiaProprietary</c>), version (<c>Vulkan 1.3.280</c>) and
    /// reasons, every one logged at <see cref="LogLevel.Information"/> in the <c>Graphics.Vulkan</c> category; an empty
    /// list or one with only rejected adapters is a success. Or the failure of instance creation (for example
    /// <see cref="ErrorKind.Unavailable"/> when the validation layer isn't installed), of the surface's creation, or of
    /// a device query, with the call's kind and code.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Creates a <c>VkInstance</c> (with the surface's instance extensions and, when asked, validation) and, with a
    /// compatible surface, one temporary <c>VkSurfaceKHR</c> to ask each queue family about presentation; it destroys
    /// both before returning, on every path, so nothing outlives the call. With a compatible surface it must run on the
    /// main thread, and not while a device presents to that window: a window has at most one surface at a time.
    /// </para>
    /// <para>The owner thread, never re-entrantly. Allocates.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The request's logger, or the compatible surface's window, is disposed.</exception>
    public Result<IReadOnlyList<GraphicsAdapterInfo>> EnumerateAdapters(BackendDeviceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AdapterEnumeration.Enumerate(AdapterEnumeration.Request.From(request, _validationAllowList), new VulkanCallGate());
    }

    // The decision's own words, from a table rather than Enum.ToString (no reflection in the backend).
    private static string OriginName(LoaderOrigin origin) => origin switch
    {
        LoaderOrigin.Bundled => "bundled",
        LoaderOrigin.Explicit => "explicit",
        _ => "system",
    };

    private static void Validate(VulkanBackendOptions options)
    {
        if (options.AppDirectory is null || !Path.IsPathFullyQualified(options.AppDirectory))
        {
            throw new ArgumentException("VulkanBackendOptions.AppDirectory must be a fully qualified path.", nameof(options));
        }

        if (options.Logger is null)
        {
            throw new ArgumentException("VulkanBackendOptions.Logger must not be null.", nameof(options));
        }

        if (options.LoaderPath is not null && !Path.IsPathFullyQualified(options.LoaderPath))
        {
            throw new ArgumentException("VulkanBackendOptions.LoaderPath must be a fully qualified path when set.", nameof(options));
        }

        ReadOnlySpan<ValidationAllowance> allowList = options.ValidationAllowList.Span;
        for (int i = 0; i < allowList.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(allowList[i].Reason))
            {
                throw new ArgumentException(
                    $"VulkanBackendOptions.ValidationAllowList[{i}] (message id {allowList[i].MessageIdNumber}) has no Reason: say why the message is tolerated.", nameof(options));
            }
        }
    }
}
