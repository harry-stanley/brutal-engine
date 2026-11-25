using System;

namespace BrutalEngine.Core.Logging;

/// <summary>One log entry, as passed to <see cref="ILogSink.Write"/>.</summary>
/// <remarks>
/// <para>
/// A <see langword="ref"/> struct because <see cref="Message"/> is a view of a pooled buffer that is valid only
/// during the <see cref="ILogSink.Write"/> call. A sink that keeps the message must copy it.
/// </para>
/// <para>
/// Properties are init-only so sink tests can build entries. <c>default(LogEntry)</c> has empty strings and an
/// empty message. Immutable; no member allocates.
/// </para>
/// </remarks>
public readonly ref struct LogEntry
{
    private readonly string? _category;
    private readonly string? _callerFilePath;

    /// <summary>Severity of the entry. Never <see cref="LogLevel.None"/> for entries from a logger.</summary>
    public LogLevel Level { get; init; }

    /// <summary>Name of the category the entry was logged through.</summary>
    public string Category
    {
        get => _category ?? string.Empty;
        init => _category = value;
    }

    /// <summary>When the entry was logged, from the logger's <see cref="System.TimeProvider"/> (UTC).</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Managed thread id of the thread that logged the entry.</summary>
    public int ThreadId { get; init; }

    /// <summary>The formatted message as valid UTF-8. Valid only during <see cref="ILogSink.Write"/>.</summary>
    public ReadOnlySpan<byte> Message { get; init; }

    /// <summary>Source file of the logging call, as recorded by the compiler. Empty if unknown.</summary>
    public string CallerFilePath
    {
        get => _callerFilePath ?? string.Empty;
        init => _callerFilePath = value;
    }

    /// <summary>Source line of the logging call. <c>0</c> if unknown.</summary>
    public int CallerLineNumber { get; init; }
}
