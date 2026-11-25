using System;
using BrutalEngine.Core.Logging;

namespace BrutalEngine.Core.Tests.Logging;

internal sealed record RecordedEntry(
    LogLevel Level,
    string Category,
    DateTimeOffset Timestamp,
    int ThreadId,
    string Message,
    string CallerFilePath,
    int CallerLineNumber);
