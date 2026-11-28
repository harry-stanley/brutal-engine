using System;
using System.Collections.Generic;
using BrutalEngine.Core;

namespace BrutalEngine.Graphics.Tests.DeviceLoss;

/// <summary>
/// A resource owner that records each call, with its name, into a log the test shares between owners, and runs an
/// optional hook in each.
/// </summary>
internal sealed class RecordingOwner(string name, List<string> log) : IGpuResourceOwner
{
    public string Name { get; } = name;

    /// <summary>What <see cref="OnDeviceRecreated"/> returns; success by default.</summary>
    public Result Recreated { get; set; } = Result.Success;

    /// <summary>Runs in <see cref="OnDeviceLost"/>, after the call is logged.</summary>
    public Action<GraphicsDevice>? WhenLost { get; set; }

    /// <summary>Runs in <see cref="OnDeviceRecreated"/>, after the call is logged.</summary>
    public Action<GraphicsDevice>? WhenRecreated { get; set; }

    public void OnDeviceLost(GraphicsDevice device)
    {
        log.Add($"lost {Name}");
        WhenLost?.Invoke(device);
    }

    public Result OnDeviceRecreated(GraphicsDevice device)
    {
        log.Add($"recreated {Name}");
        WhenRecreated?.Invoke(device);
        return Recreated;
    }
}
