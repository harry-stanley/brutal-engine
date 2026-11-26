namespace BrutalEngine.Graphics.Vulkan.Loader;

/// <summary>The loader decision <see cref="LoaderSetup.Configure"/> returns; the setup stores nothing.</summary>
/// <param name="PreloadedPath">
/// The absolute path of the loader that was preloaded and that BRUTAL bound (bundled or explicit), or
/// <see langword="null"/> for the system loader.
/// </param>
/// <param name="Origin">Where the loader came from.</param>
/// <param name="DriverFiles">
/// On macOS, the driver manifests the loader reads: the user's <c>VK_DRIVER_FILES</c> or <c>VK_ICD_FILENAMES</c> when
/// either was set, otherwise the bundled MoltenVK manifest the setup set, or <see langword="null"/> when there is none.
/// Always <see langword="null"/> on Windows and Linux, whose drivers are never touched.
/// </param>
internal readonly record struct LoaderDecision(string? PreloadedPath, LoaderOrigin Origin, string? DriverFiles);
