namespace BrutalEngine.Core.Logging;

/// <summary>Marker for <see cref="LogLevel.Error"/>, used as the type argument of the Error message handler.</summary>
/// <remarks>Empty and stateless.</remarks>
public readonly struct ErrorLevel : ILogLevel
{
    /// <inheritdoc />
    public static LogLevel Level => LogLevel.Error;
}
