using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>A snapshot of a graphics device's counters (docs/design/graphics.md section 13).</summary>
/// <param name="FramesSubmitted">The frames submitted so far; 0 until frames land.</param>
/// <param name="UploadBytesLastFrame">
/// The bytes the last submitted frame's writes uploaded, through the upload ring, pending upload blocks or overflow blocks
/// (a texture write counts its texels); 0 for a frame without writes.
/// </param>
/// <param name="UploadOverflowBytes">The bytes uploaded outside the ring because it was full; 0 until uploads land.</param>
/// <param name="LiveObjects">The GPU objects alive on the device, over every kind, default texture views included.</param>
/// <param name="ValidationErrors">The error messages the backend's validation layer has reported; 0 without validation.</param>
/// <param name="ValidationWarnings">The warning messages the backend's validation layer has reported; 0 without validation.</param>
/// <remarks>
/// Taken by <see cref="GraphicsDevice.Statistics"/>; it doesn't change afterwards. Equality compares every field.
/// Immutable and safe to copy between threads. No member allocates, except <see cref="ToString"/>.
/// </remarks>
public readonly record struct GraphicsStatistics(
    long FramesSubmitted,
    long UploadBytesLastFrame,
    long UploadOverflowBytes,
    int LiveObjects,
    long ValidationErrors,
    long ValidationWarnings)
{
    /// <summary>
    /// Returns every field in the record's usual form, for example <c>GraphicsStatistics { FramesSubmitted = 0, ...,
    /// LiveObjects = 3, ... }</c>.
    /// </summary>
    /// <returns>A diagnostic string, formatted with the invariant culture whatever the current culture is. Allocates.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"GraphicsStatistics {{ FramesSubmitted = {FramesSubmitted}, UploadBytesLastFrame = {UploadBytesLastFrame}, UploadOverflowBytes = {UploadOverflowBytes}, "
        + $"LiveObjects = {LiveObjects}, ValidationErrors = {ValidationErrors}, ValidationWarnings = {ValidationWarnings} }}");
}
