using System.Runtime.CompilerServices;
using BrutalEngine.Core;
using BrutalEngine.Graphics;
using BrutalEngine.Graphics.Backend;

namespace BrutalEngine.TestUtilities.Tests;

/// <summary>
/// The internal Graphics members these tests need to stand in for the front end, reached with
/// <see cref="UnsafeAccessorAttribute"/>: Graphics grants internals only to its own tests and conformance suite, and only
/// the front end creates handles and records table entries. Each accessor binds by name and signature when first called,
/// so a renamed member fails the test that uses it with <see cref="System.MissingMethodException"/>.
/// </summary>
internal static class GraphicsInternals
{
    /// <summary><c>TextureHandle(Handle value)</c>.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    public static extern TextureHandle NewTextureHandle(Handle value);

    /// <summary><c>BackendObjectTable.Set(BackendObjectKind kind, Handle handle, ulong id)</c>.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Set")]
    public static extern void SetEntry(BackendObjectTable table, BackendObjectKind kind, Handle handle, ulong id);
}
