using System.Globalization;

namespace BrutalEngine.Graphics.Backend;

/// <summary>A buffer a backend created: its id and, for host memory, its mapped pointer.</summary>
/// <param name="Id">The buffer's id: non-zero, chosen by the backend and opaque to the front end.</param>
/// <param name="MappedPointer">
/// The start of the buffer's persistently mapped memory for <see cref="BackendMemory.HostUpload"/> and
/// <see cref="BackendMemory.HostReadback"/>; 0 for the device-local kinds.
/// </param>
/// <remarks>
/// <para>
/// The backend owns the buffer and its mapping. The pointer stays valid until the front end destroys the buffer (or the
/// backend device is disposed), and spans the buffer's whole size. Reading or writing it follows the memory rules on
/// <see cref="BackendMemory"/>.
/// </para>
/// <para>
/// Equality compares both fields. Immutable and safe to copy between threads. No member allocates, except
/// <see cref="ToString"/>.
/// </para>
/// </remarks>
public readonly record struct BackendBuffer(ulong Id, nint MappedPointer)
{
    /// <summary>
    /// Returns both fields in the record's usual form, for example <c>BackendBuffer { Id = 4294967297, MappedPointer = 0 }</c>.
    /// </summary>
    /// <returns>A diagnostic string, formatted with the invariant culture whatever the current culture is. Allocates.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture, $"BackendBuffer {{ Id = {Id}, MappedPointer = {MappedPointer} }}");
}
