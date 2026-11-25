namespace BrutalEngine.Core.Logging;

/// <summary>Marker for <see cref="LogLevel.Information"/>, used as the type argument of the Information message handler.</summary>
/// <remarks>Empty and stateless.</remarks>
public readonly struct InformationLevel : ILogLevel
{
    /// <inheritdoc />
    public static LogLevel Level => LogLevel.Information;
}
