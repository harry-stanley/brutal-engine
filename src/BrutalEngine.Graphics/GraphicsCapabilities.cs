namespace BrutalEngine.Graphics;

/// <summary>What a created graphics device can do: its enabled features and the adapter's limits.</summary>
/// <remarks>
/// The backend fills this in once, when it creates the device, and it doesn't change for the device's lifetime.
/// Immutable and safe to read from any thread. No member allocates, except <see cref="object.ToString"/>, which
/// formats <see cref="Limits"/> with the invariant culture.
/// </remarks>
public sealed record GraphicsCapabilities
{
    /// <summary>The enabled features: every required feature plus the optional ones the adapter has.</summary>
    public required GraphicsFeatures Features { get; init; }

    /// <summary>The adapter's limits.</summary>
    public required GraphicsLimits Limits { get; init; }
}
