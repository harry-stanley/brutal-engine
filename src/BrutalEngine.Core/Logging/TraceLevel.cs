namespace BrutalEngine.Core.Logging;

/// <summary>Marker for <see cref="LogLevel.Trace"/>, used as the type argument of the Trace message handler.</summary>
/// <remarks>Empty and stateless.</remarks>
public readonly struct TraceLevel : ILogLevel
{
    /// <inheritdoc />
    public static LogLevel Level => LogLevel.Trace;
}
