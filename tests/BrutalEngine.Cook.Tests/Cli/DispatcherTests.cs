using System;
using System.IO;
using BrutalEngine.Cook.Cli;
using Xunit;

namespace BrutalEngine.Cook.Tests.Cli;

public sealed class DispatcherTests : IDisposable
{
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();

    public void Dispose()
    {
        _stdout.Dispose();
        _stderr.Dispose();
    }

    [Fact]
    public void Run_NoVerb_ReturnsUsage()
    {
        CookExitCode exit = Dispatcher.Run([], _stdout, _stderr);

        Assert.Equal(CookExitCode.Usage, exit);
        Assert.Equal(64, (int)exit);
        Assert.Contains("no verb given", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage: brutal-cook <verb>", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(_stdout.ToString());
    }

    [Theory]
    [InlineData("assets")]
    [InlineData("Shader")]
    [InlineData("shader ")]
    [InlineData("--help", "shader")]
    [InlineData("build", "--help")]
    public void Run_UnknownVerb_ReturnsUsage(params string[] args)
    {
        CookExitCode exit = Dispatcher.Run(args, _stdout, _stderr);

        Assert.Equal(CookExitCode.Usage, exit);
        Assert.Contains($"unknown verb '{args[0]}'", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage: brutal-cook <verb>", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(_stdout.ToString());
    }

    [Fact]
    public void Run_Help_ReturnsSuccess()
    {
        CookExitCode exit = Dispatcher.Run(["--help"], _stdout, _stderr);

        Assert.Equal(CookExitCode.Success, exit);
        Assert.Contains("Usage: brutal-cook <verb>", _stdout.ToString(), StringComparison.Ordinal);
        Assert.Empty(_stderr.ToString());
    }
}
