using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace BrutalEngine.Graphics;

/// <summary>
/// Thrown by <see cref="GraphicsDevice.ThrowIfLeaks"/> when GPU objects are still alive: the GPU counterpart of the
/// unmanaged-memory leak check (docs/design/graphics.md section 13).
/// </summary>
/// <remarks>
/// Immutable: the list constructor keeps a read-only copy of its list, so the exception is safe to read from any thread,
/// and the message lists the same lines as <see cref="LeakedObjects"/>.
/// </remarks>
public sealed class GpuResourceLeakException : Exception
{
    /// <summary>Creates the exception for a list of live objects.</summary>
    /// <param name="leakedObjects">
    /// One <c>"Kind 'DebugName'"</c> line per live object. The exception keeps a read-only copy, so later changes to the
    /// caller's list don't reach it.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="leakedObjects"/> is <see langword="null"/>.</exception>
    /// <remarks>Allocates the copy and the message.</remarks>
    public GpuResourceLeakException(IReadOnlyList<string> leakedObjects)
        : this(Copy(leakedObjects))
    {
    }

    /// <summary>Creates the exception with a default message and no leaked objects.</summary>
    public GpuResourceLeakException()
        : base("GPU objects were leaked.")
    {
    }

    /// <summary>Creates the exception with a custom message and no leaked objects.</summary>
    /// <param name="message">The message.</param>
    public GpuResourceLeakException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a custom message, an inner exception and no leaked objects.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public GpuResourceLeakException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    // The list constructor's body: the message and the property are made from the same copy.
    private GpuResourceLeakException(ReadOnlyCollection<string> leakedObjects)
        : base(Format(leakedObjects))
    {
        LeakedObjects = leakedObjects;
    }

    /// <summary>
    /// One line per object still alive, oldest first, in the form <c>Kind 'DebugName'</c>, for example
    /// <c>Buffer 'Vertices'</c> (<c>''</c> when the object has no debug name). Empty when created without a list.
    /// </summary>
    public IReadOnlyList<string> LeakedObjects { get; } = [];

    private static ReadOnlyCollection<string> Copy(IReadOnlyList<string> leakedObjects)
    {
        ArgumentNullException.ThrowIfNull(leakedObjects);
        string[] copy = new string[leakedObjects.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            copy[i] = leakedObjects[i];
        }

        return Array.AsReadOnly(copy);
    }

    private static string Format(ReadOnlyCollection<string> leakedObjects) =>
        $"{leakedObjects.Count} GPU object(s) still alive: {string.Join(", ", leakedObjects)}.";
}
