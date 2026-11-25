namespace BrutalEngine.Core.Logging;

/// <summary>Receives the log entries of one or more loggers, e.g. to write them to the console or a file.</summary>
/// <remarks>
/// <para>
/// A logger calls its sinks one entry at a time, in order, while holding its dispatch lock, so a sink used by a
/// single logger needs no locking of its own. A sink shared by several loggers must synchronise itself.
/// </para>
/// <para>
/// An exception thrown by a sink (other than <see cref="System.OutOfMemoryException"/>) is caught by the
/// logger and counted; it doesn't reach the code that logged. Sinks must not log through the logger that calls
/// them: such entries are dropped.
/// </para>
/// <para>The logger doesn't own its sinks. Whoever creates a sink disposes it, after the logger.</para>
/// </remarks>
public interface ILogSink
{
    /// <summary>Writes one entry.</summary>
    /// <param name="entry">
    /// The entry. Its <see cref="LogEntry.Message"/> points into a pooled buffer that is reused after this call
    /// returns: copy it to keep it.
    /// </param>
    public void Write(scoped in LogEntry entry);

    /// <summary>Writes out anything the sink has buffered.</summary>
    public void Flush();
}
