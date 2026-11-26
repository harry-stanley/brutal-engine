using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using BrutalEngine.Core.Logging;

namespace BrutalEngine.Graphics.Vulkan.Tests.Validation;

/// <summary>
/// Test double: a sink that counts every entry and, unless <see cref="Record"/> is off, copies its level and text.
/// Counting alone allocates nothing, so the allocation test can use it.
/// </summary>
internal sealed class ValidationLog : ILogSink
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];
    private int _count;

    public bool Record { get; init; } = true;

    public int Count => Volatile.Read(ref _count);

    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

    public void Write(scoped in LogEntry entry)
    {
        _ = Interlocked.Increment(ref _count);
        if (Record)
        {
            lock (_entries)
            {
                _entries.Add((entry.Level, Encoding.UTF8.GetString(entry.Message)));
            }
        }
    }

    public void Flush()
    {
    }

    /// <summary>A logger over a new sink at <see cref="LogLevel.Trace"/>, and its <c>Graphics.Validation</c> category.</summary>
    public static (Logger Logger, LogCategory Category) Create(ValidationLog sink, TimeProvider? time = null)
    {
        Logger logger = new(new LoggerOptions { Sinks = [sink], MinimumLevel = LogLevel.Trace, TimeProvider = time ?? TimeProvider.System });
        return (logger, logger.CreateCategory("Graphics.Validation"));
    }
}
