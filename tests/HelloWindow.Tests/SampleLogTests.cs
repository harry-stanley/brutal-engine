using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BrutalEngine.Core.Logging.Sinks;
using HelloWindow.Splash;
using Xunit;

namespace HelloWindow.Tests;

public sealed partial class SampleLogTests
{
    [Fact]
    public void App_Information_WritesOneLineToOutput()
    {
        using var output = new MemoryStream();
        using var errors = new MemoryStream();
        using (var log = new SampleLog(new ConsoleLogSinkOptions { Output = output, ErrorOutput = errors }))
        {
            log.App.Information($"Swapchain {1280}x{720}");
        }

        Assert.Matches(Line("INFO", "HelloWindow", "Swapchain 1280x720"), Text(output));
        Assert.Equal(string.Empty, Text(errors));
    }

    [Fact]
    public void Vulkan_Error_WritesOneLineToErrorOutput()
    {
        using var output = new MemoryStream();
        using var errors = new MemoryStream();
        using (var log = new SampleLog(new ConsoleLogSinkOptions { Output = output, ErrorOutput = errors }))
        {
            log.Vulkan.Error($"vkQueueSubmit returned ErrorDeviceLost (-4).");
        }

        Assert.Equal(string.Empty, Text(output));
        Assert.Matches(Line("EROR", "HelloWindow.Vulkan", "vkQueueSubmit returned ErrorDeviceLost \\(-4\\)\\."), Text(errors));
    }

    [Fact]
    public void App_Warning_GoesToErrorOutput()
    {
        using var output = new MemoryStream();
        using var errors = new MemoryStream();
        using (var log = new SampleLog(new ConsoleLogSinkOptions { Output = output, ErrorOutput = errors }))
        {
            log.App.Warning($"careful");
        }

        Assert.Equal(string.Empty, Text(output));
        Assert.Matches(Line("WARN", "HelloWindow", "careful"), Text(errors));
    }

    [Fact]
    public void App_SplashStatistics_MatchesTheCiGrep()
    {
        var statistics = new SplashStatistics();
        statistics.Presented(splash: true, timestamp: 0);
        statistics.Presented(splash: true, timestamp: 1_000);
        statistics.Presented(splash: false, timestamp: 2_000);
        using var output = new MemoryStream();
        using (var log = new SampleLog(new ConsoleLogSinkOptions { Output = output, ErrorOutput = output }))
        {
            log.App.Information($"{statistics.Format(1_000)}");
        }

        // The pattern the CI's vulkan-smoke job greps the HelloWindow logs with (JIT and NativeAOT runs).
        Assert.Matches(CiSplashGrep(), Text(output));
    }

    [Fact]
    public void App_ErrorOfTheLargestMessageSize_IsWrittenWhole()
    {
        // 65536 bytes: the largest message the logger accepts, and 16 times its default limit.
        string message = new('x', 65_536);
        using var output = new MemoryStream();
        using var errors = new MemoryStream();
        using (var log = new SampleLog(new ConsoleLogSinkOptions { Output = output, ErrorOutput = errors }))
        {
            log.App.Error($"{message}");
        }

        Assert.Matches(Line("EROR", "HelloWindow", $"x{{{message.Length}}}"), Text(errors));
    }

    [Fact]
    public void App_AggregateExceptionReport_IsWrittenWhole()
    {
        // What HelloWindowApp logs when several SDL callbacks failed: BRUTAL wraps up to 16 of their exceptions in
        // one AggregateException, reported with its ToString() as a single entry.
        var inner = new Exception[16];
        for (int i = 0; i < inner.Length; i++)
        {
            inner[i] = Thrown(i);
        }

        string report = new AggregateException("SDL callbacks failed.", inner).ToString();
        Assert.True(Encoding.UTF8.GetByteCount(report) > 4096, "The report must be longer than the default limit.");
        using var output = new MemoryStream();
        using var errors = new MemoryStream();
        using (var log = new SampleLog(new ConsoleLogSinkOptions { Output = output, ErrorOutput = errors }))
        {
            log.App.Error($"{report}");
        }

        Assert.EndsWith($" EROR [HelloWindow] {report}\n", Text(errors), StringComparison.Ordinal);
    }

    private static InvalidOperationException Thrown(int index)
    {
        try
        {
            throw new InvalidOperationException($"Callback {index} failed while handling an event of the sample.");
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }

    private static string Line(string level, string category, string messagePattern) =>
        $"^[0-9]{{4}}-[0-9]{{2}}-[0-9]{{2}}T[0-9]{{2}}:[0-9]{{2}}:[0-9]{{2}}\\.[0-9]{{3}}Z {level} \\[{Regex.Escape(category)}\\] {messagePattern}\n$";

    private static string Text(MemoryStream stream) => Encoding.UTF8.GetString(stream.ToArray());

    [GeneratedRegex(@" INFO \[HelloWindow\] splash: [1-9][0-9]* frames over [0-9]+\.[0-9]{2} s, scene: [1-9][0-9]* frames$", RegexOptions.Multiline)]
    private static partial Regex CiSplashGrep();
}
