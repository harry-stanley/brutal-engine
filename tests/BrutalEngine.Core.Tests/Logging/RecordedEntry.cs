using System;
using System.Text;
using BrutalEngine.Core.Logging;

namespace BrutalEngine.Core.Tests.Logging;

internal sealed record RecordedEntry(
    LogLevel Level,
    string Category,
    DateTimeOffset Timestamp,
    int ThreadId,
    byte[] MessageBytes,
    string CallerFilePath,
    int CallerLineNumber)
{
    public string Message => Encoding.UTF8.GetString(MessageBytes);
}
