using System.Collections.Generic;

namespace HelloWindow;

/// <summary>The result of <see cref="DeviceSelector.Select"/>.</summary>
/// <param name="ChosenIndex">The index of the chosen candidate, or -1 if none is suitable.</param>
/// <param name="Rejections">One line per rejected candidate: its name and every reason it was rejected.</param>
internal sealed record DeviceSelection(int ChosenIndex, IReadOnlyList<string> Rejections)
{
    /// <summary>Gets a value indicating whether a suitable device was found.</summary>
    public bool Found => ChosenIndex >= 0;

    /// <summary>Gets the setup-failure message listing every rejected device and why.</summary>
    public string FailureMessage =>
        "No Vulkan 1.3 GPU with graphics+present found: "
        + (Rejections.Count == 0 ? "the Vulkan loader reported no devices" : string.Join("; ", Rejections));
}
