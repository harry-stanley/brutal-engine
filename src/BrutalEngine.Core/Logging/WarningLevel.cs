namespace BrutalEngine.Core.Logging;

/// <summary>Marker for <see cref="LogLevel.Warning"/>, used as the type argument of the Warning message handler.</summary>
/// <remarks>Empty and stateless.</remarks>
public readonly struct WarningLevel : ILogLevel
{
    /// <inheritdoc />
    public static LogLevel Level => LogLevel.Warning;
}
