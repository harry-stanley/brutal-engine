namespace BrutalEngine.Core.Logging;

/// <summary>Severity of a log entry, and the threshold a category filters by.</summary>
/// <remarks>Levels are ordered: a category whose level is <see cref="Warning"/> logs <see cref="Warning"/>,
/// <see cref="Error"/> and <see cref="Critical"/> entries.</remarks>
public enum LogLevel : byte
{
    /// <summary>Very detailed tracing, normally off.</summary>
    Trace = 0,

    /// <summary>Diagnostic detail for development.</summary>
    Debug = 1,

    /// <summary>Normal operation worth recording. The default minimum level.</summary>
    Information = 2,

    /// <summary>Something unexpected that the engine recovered from.</summary>
    Warning = 3,

    /// <summary>An operation failed.</summary>
    Error = 4,

    /// <summary>A failure that likely ends the process or session.</summary>
    Critical = 5,

    /// <summary>Filter only: as a threshold it turns a category off. Entries are never logged at this level.</summary>
    None = 6,
}
