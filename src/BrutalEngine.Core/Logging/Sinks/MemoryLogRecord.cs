using System;

namespace BrutalEngine.Core.Logging.Sinks;

/// <summary>One log entry copied out of a <see cref="MemoryLogSink"/> by <see cref="MemoryLogSink.CopyTo"/>.</summary>
/// <remarks>
/// <para>
/// <see cref="Message"/> is a slice of the message buffer the caller passed to <see cref="MemoryLogSink.CopyTo"/>:
/// it stays valid, and unchanged, until the caller reuses that buffer. The record doesn't reference the sink.
/// </para>
/// <para>
/// Immutable. <c>default(MemoryLogRecord)</c> has empty strings and an empty message. No member allocates.
/// </para>
/// </remarks>
public readonly struct MemoryLogRecord
{
    private readonly string? _category;
    private readonly string? _callerFilePath;

    internal MemoryLogRecord(
        LogLevel level,
        string category,
        DateTimeOffset timestamp,
        int threadId,
        ReadOnlyMemory<byte> message,
        string callerFilePath,
        int callerLineNumber)
    {
        Level = level;
        _category = category;
        Timestamp = timestamp;
        ThreadId = threadId;
        Message = message;
        _callerFilePath = callerFilePath;
        CallerLineNumber = callerLineNumber;
    }

    /// <summary>Severity of the entry.</summary>
    public LogLevel Level { get; }

    /// <summary>Name of the category the entry was logged through.</summary>
    public string Category => _category ?? string.Empty;

    /// <summary>When the entry was logged, as the logger timestamped it.</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>Managed thread id of the thread that logged the entry.</summary>
    public int ThreadId { get; }

    /// <summary>
    /// The message as valid UTF-8: a slice of the caller's message buffer. Messages longer than the sink's
    /// <see cref="MemoryLogSink.CapacityBytes"/> were cut on a code-point boundary and end with <c>…</c> (U+2026).
    /// </summary>
    public ReadOnlyMemory<byte> Message { get; }

    /// <summary>Source file of the logging call. Empty if unknown.</summary>
    public string CallerFilePath => _callerFilePath ?? string.Empty;

    /// <summary>Source line of the logging call. <c>0</c> if unknown.</summary>
    public int CallerLineNumber { get; }
}
