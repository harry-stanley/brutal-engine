using System;
using System.Collections.Generic;
using System.Text;
using BrutalEngine.Core.Logging;

namespace BrutalEngine.Core.Tests.Logging;

/// <summary>Test double: copies every entry it receives, counts flushes, and can be told to throw.</summary>
internal sealed class RecordingSink : ILogSink
{
    public List<RecordedEntry> Entries { get; } = [];

    public int FlushCount { get; private set; }

    public bool ThrowOnWrite { get; init; }

    public bool ThrowOnFlush { get; init; }

    public IReadOnlyList<string> Messages => Entries.ConvertAll(e => e.Message);

    public void Write(scoped in LogEntry entry)
    {
        if (ThrowOnWrite)
        {
            throw new InvalidOperationException("Sink failure (test).");
        }

        Entries.Add(new RecordedEntry(
            entry.Level,
            entry.Category,
            entry.Timestamp,
            entry.ThreadId,
            Encoding.UTF8.GetString(entry.Message),
            entry.CallerFilePath,
            entry.CallerLineNumber));
    }

    public void Flush()
    {
        FlushCount++;
        if (ThrowOnFlush)
        {
            throw new InvalidOperationException("Sink flush failure (test).");
        }
    }
}
