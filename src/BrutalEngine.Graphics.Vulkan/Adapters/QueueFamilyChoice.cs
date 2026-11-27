using System;

namespace BrutalEngine.Graphics.Vulkan.Adapters;

/// <summary>
/// Picks the queue family a device would use (docs/design/graphics-vulkan.md section 3, "Selection"): among the
/// families with graphics support, and with present support when the request has a compatible surface, the first that
/// also supports compute, else the first. Compute is a preference only; it never makes a family fail.
/// </summary>
/// <remarks>Stateless and thread-safe. Allocates nothing.</remarks>
internal static class QueueFamilyChoice
{
    /// <summary>Chooses the queue family a device on this adapter would use.</summary>
    /// <param name="families">The adapter's queue families, in the driver's order.</param>
    /// <param name="needsPresent">
    /// Whether the family must present to the compatible surface: <see langword="true"/> exactly when the request has one.
    /// </param>
    /// <returns>
    /// The chosen family's <see cref="AdapterCandidate.QueueFamily.Index"/>, or -1 when no family passes.
    /// </returns>
    public static int Choose(ReadOnlySpan<AdapterCandidate.QueueFamily> families, bool needsPresent)
    {
        int position = ChoosePosition(families, needsPresent);
        return position < 0 ? -1 : families[position].Index;
    }

    /// <summary>Chooses as <see cref="Choose"/> does, but returns the chosen family's position in the span.</summary>
    /// <param name="families">The adapter's queue families, in the driver's order.</param>
    /// <param name="needsPresent">Whether the family must present to the compatible surface.</param>
    /// <returns>The position of the chosen family in <paramref name="families"/>, or -1 when no family passes.</returns>
    public static int ChoosePosition(ReadOnlySpan<AdapterCandidate.QueueFamily> families, bool needsPresent)
    {
        int firstPassing = -1;
        for (int i = 0; i < families.Length; i++)
        {
            AdapterCandidate.QueueFamily family = families[i];
            if (!family.Graphics || (needsPresent && !family.Present))
            {
                continue;
            }

            if (family.Compute)
            {
                return i;
            }

            if (firstPassing < 0)
            {
                firstPassing = i;
            }
        }

        return firstPassing;
    }
}
