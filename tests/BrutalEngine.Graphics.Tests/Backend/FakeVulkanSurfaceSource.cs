using System;
using BrutalEngine.Core;
using BrutalEngine.Platform;

namespace BrutalEngine.Graphics.Tests.Backend;

/// <summary>
/// A test surface source that returns scripted extension names and a scripted surface result, records the instance it
/// was given, and throws <see cref="Failure"/> from every member when one is set.
/// </summary>
internal sealed class FakeVulkanSurfaceSource(byte[][] extensions, Result<nint> surface) : IVulkanSurfaceSource
{
    public Exception? Failure { get; set; }

    public nint LastInstance { get; private set; }

    public int CreateCalls { get; private set; }

    public int RequiredInstanceExtensionCount => Failure is null ? extensions.Length : throw Failure;

    public ReadOnlySpan<byte> GetRequiredInstanceExtension(int index) => Failure is null ? extensions[index] : throw Failure;

    public Result<nint> CreateVulkanSurface(nint instance)
    {
        CreateCalls++;
        LastInstance = instance;
        return Failure is null ? surface : throw Failure;
    }
}
