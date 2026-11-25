using System;
using BrutalEngine.Core.Logging;
using BrutalEngine.Core.Logging.Sinks;

namespace HelloWindow;

/// <summary>The sample's log: a BrutalEngine.Core <see cref="Logger"/> writing to a <see cref="ConsoleLogSink"/>,
/// with the sample's two categories.</summary>
/// <remarks>
/// <para>
/// Each entry is one UTF-8 line, <c>yyyy-MM-ddTHH:mm:ss.fffZ LEVEL [Category] message</c> (docs/design/logging.md).
/// <c>Information</c> goes to standard output, <c>Warning</c> and above to standard error (the sink's default
/// threshold), which is where the sample has always sent progress and problems.
/// </para>
/// <para>
/// Nothing is static: <c>Program</c> owns the only instance and passes the categories down. Logging through a category
/// is thread-safe (the validation layer may log from any thread) and, after <see cref="Dispose"/>, a silent no-op.
/// </para>
/// </remarks>
internal sealed class SampleLog : IDisposable
{
    /// <summary>The category of the sample's own progress and failures.</summary>
    public const string AppCategoryName = "HelloWindow";

    /// <summary>The category of the Vulkan setup and the validation layer's messages.</summary>
    public const string VulkanCategoryName = "HelloWindow.Vulkan";

    // The logger's largest message size (its default is 4096 bytes). A failure report is one entry holding the whole
    // exception.ToString(), which for an AggregateException of up to 16 SDL callback failures, or a deep stack trace,
    // can be far longer than the default; a cut report ends in an ellipsis and loses the frames that matter.
    private const int MaxMessageBytes = 65_536;

    private readonly ConsoleLogSink _sink;
    private readonly Logger _logger;

    /// <summary>Initializes a new instance of the <see cref="SampleLog"/> class.</summary>
    /// <param name="sinkOptions">The console sink's options: the default writes to the standard streams; tests inject
    /// streams.</param>
    public SampleLog(ConsoleLogSinkOptions sinkOptions)
    {
        _sink = new ConsoleLogSink(sinkOptions);
        try
        {
            _logger = new Logger(new LoggerOptions { Sinks = [_sink], MaxMessageBytes = MaxMessageBytes });
            App = _logger.CreateCategory(AppCategoryName);
            Vulkan = _logger.CreateCategory(VulkanCategoryName);
        }
        catch
        {
            _logger?.Dispose();
            _sink.Dispose();
            throw;
        }
    }

    /// <summary>Gets the <c>HelloWindow</c> category.</summary>
    public LogCategory App { get; }

    /// <summary>Gets the <c>HelloWindow.Vulkan</c> category.</summary>
    public LogCategory Vulkan { get; }

    /// <summary>Flushes and disposes the logger, then the sink (which closes only the standard streams it opened).</summary>
    public void Dispose()
    {
        _logger.Dispose();
        _sink.Dispose();
    }
}
