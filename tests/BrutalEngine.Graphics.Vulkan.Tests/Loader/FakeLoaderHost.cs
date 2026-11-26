using System;
using System.Collections.Generic;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Vulkan.Loader;

namespace BrutalEngine.Graphics.Vulkan.Tests.Loader;

/// <summary>
/// A scripted process for <see cref="LoaderSetup"/>: files, an environment, loadable libraries, and a BRUTAL that binds
/// once, on its first touch, to the first library preloaded before it (BRUTAL's bare-name lookup reuses a library
/// already in the process) or else to <see cref="SystemEntryPoint"/>. Every effect is recorded in order.
/// </summary>
internal sealed class FakeLoaderHost : IVulkanLoaderHost
{
    private nint? _bound;
    private nint _firstPreloaded;

    public bool IsMacOS { get; init; }

    public HashSet<string> Files { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Environment { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the libraries <see cref="Preload"/> can load, by path, with their entry points.</summary>
    public Dictionary<string, nint> Libraries { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the entry point BRUTAL binds when nothing was preloaded; 0 means BRUTAL finds no loader.</summary>
    public nint SystemEntryPoint { get; set; }

    /// <summary>Gets the failure <see cref="SetEnvironmentVariable"/> returns, if any.</summary>
    public EngineError? SetEnvironmentFailure { get; set; }

    public List<string> Effects { get; } = [];

    public static EngineError PreloadFailure(string path) => new(ErrorKind.Unavailable, "fake: cannot load " + path);

    public FakeLoaderHost WithEnvironment(string name, string value)
    {
        Environment[name] = value;
        return this;
    }

    public FakeLoaderHost WithoutSystemLoader()
    {
        SystemEntryPoint = 0;
        return this;
    }

    public FakeLoaderHost WithoutLibraries()
    {
        Libraries.Clear();
        return this;
    }

    public FakeLoaderHost WithSetenvFailure(EngineError failure)
    {
        SetEnvironmentFailure = failure;
        return this;
    }

    public bool FileExists(string path) => Files.Contains(path);

    public string? GetEnvironmentVariable(string name) => Environment.GetValueOrDefault(name);

    public Result SetEnvironmentVariable(string name, string value)
    {
        Effects.Add($"setenv {name}={value}");
        if (SetEnvironmentFailure is { } failure)
        {
            return failure;
        }

        Environment[name] = value;
        return Result.Success;
    }

    public Result<nint> Preload(string path)
    {
        Effects.Add("preload " + path);
        if (!Libraries.TryGetValue(path, out nint entryPoint))
        {
            return PreloadFailure(path);
        }

        if (_firstPreloaded == 0)
        {
            _firstPreloaded = entryPoint;
        }

        return entryPoint;
    }

    public nint BrutalGetInstanceProcAddr()
    {
        _bound ??= _firstPreloaded != 0 ? _firstPreloaded : SystemEntryPoint;
        return _bound.Value;
    }
}
