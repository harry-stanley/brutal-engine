using System.Globalization;

namespace BrutalEngine.Graphics.Backend;

/// <summary>A swapchain image a frame acquired: the frame's work waits for it, and leaves it presentable.</summary>
/// <param name="Swapchain">The swapchain's id.</param>
/// <param name="ImageIndex">The image's index, as the acquire returned it.</param>
/// <remarks>
/// <para>
/// The front end lists one per image acquired in the frame, in acquire order; commands that refer to an acquired image
/// by position index into that list. After the frame's work is submitted, the front end presents each one.
/// </para>
/// <para>
/// A plain unmanaged value. Equality compares both fields. Immutable and safe to copy between threads. No member
/// allocates, except <see cref="ToString"/>.
/// </para>
/// </remarks>
public readonly record struct SwapchainImageUse(ulong Swapchain, int ImageIndex)
{
    /// <summary>Returns both fields in the record's usual form, for example <c>SwapchainImageUse { Swapchain = 3, ImageIndex = 1 }</c>.</summary>
    /// <returns>A diagnostic string, formatted with the invariant culture whatever the current culture is. Allocates.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture, $"SwapchainImageUse {{ Swapchain = {Swapchain}, ImageIndex = {ImageIndex} }}");
}
