using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Xunit;

namespace BrutalEngine.Analyzers.Tests;

public sealed class DiscardedStatusResultAnalyzerTests
{
    // A stand-in for BRUTAL's Vulkan enum with the same metadata name (Brutal.VulkanApi.VkResult, see
    // BRUTAL Brutal.Vulkan/Generated/Enums/VkResult.gen.cs:135), so the tests don't need the native package.
    private const string VulkanStub = """
        namespace Brutal.VulkanApi
        {
            public enum VkResult { Success = 0, ErrorDeviceLost = -4 }

            public static class Device
            {
                public static VkResult WaitIdle() => VkResult.Success;
            }
        }
        """;

    private const string SampleHeader = """
        namespace Sample
        {
            using System.Threading.Tasks;
            using BrutalEngine.Core;
            using Brutal.VulkanApi;

            public sealed class Other { public static Other Create() => new(); }

            public sealed class Target
            {
                public static Result DoWork() => Result.Success;
                public static Result<int> Load() => 1;
                public static Task<Result> DoWorkAsync() => Task.FromResult(Result.Success);
                public static int Count() => 1;
                public static void Nothing() { }
                public Result Instance() => Result.Success;

                public static async Task Body(Target? maybe)
                {
        """;

    private const string SampleFooter = """

                    await Task.CompletedTask;
                }
            }
        }
        """;

    private static Task<ImmutableArray<Diagnostic>> RunAsync(string body, IReadOnlyDictionary<string, string>? options = null) =>
        AnalyzerHarness.RunAsync(
            new DiscardedStatusResultAnalyzer(),
            VulkanStub + "\n" + SampleHeader + "\n" + body + SampleFooter,
            options,
            TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("DoWork();", "Result", "DoWork")]
    [InlineData("Load();", "Result", "Load")]
    [InlineData("Device.WaitIdle();", "VkResult", "WaitIdle")]
    [InlineData("await DoWorkAsync();", "Result", "DoWorkAsync")]
    [InlineData("maybe?.Instance();", "Result", "Instance")]
    public async Task DiscardedStatus_IsReported(string statement, string typeName, string methodName)
    {
        ImmutableArray<Diagnostic> diagnostics = await RunAsync(statement);

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiscardedStatusResultAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        string message = diagnostic.GetMessage(CultureInfo.InvariantCulture);
        Assert.Contains($"The {typeName} returned by '{methodName}'", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("_ = DoWork();")]
    [InlineData("_ = Device.WaitIdle();")]
    [InlineData("Result r = DoWork(); r.ThrowIfFailed();")]
    [InlineData("if (DoWork().IsFailure) { return; }")]
    [InlineData("int v = Load().GetValueOrDefault(0);")]
    [InlineData("Count();")]
    [InlineData("Nothing();")]
    [InlineData("Other.Create();")]
    [InlineData("_ = await DoWorkAsync();")]
    [InlineData("_ = DoWorkAsync();")]
    public async Task CheckedOrNonStatus_IsNotReported(string statement)
    {
        Assert.Empty(await RunAsync(statement));
    }

    [Fact]
    public async Task ConfiguredAdditionalType_IsReported()
    {
        Dictionary<string, string> options = new()
        {
            [DiscardedStatusResultAnalyzer.AdditionalTypesOptionKey] = "Sample.Other ; Does.Not.Exist",
        };

        Diagnostic diagnostic = Assert.Single(await RunAsync("Other.Create();", options));
        Assert.Equal(DiscardedStatusResultAnalyzer.DiagnosticId, diagnostic.Id);
    }

    [Fact]
    public async Task Location_CoversTheInvocation()
    {
        Diagnostic diagnostic = Assert.Single(await RunAsync("Device.WaitIdle();"));

        Assert.Equal("Device.WaitIdle()", diagnostic.Location.SourceTree!.GetText(TestContext.Current.CancellationToken).ToString(diagnostic.Location.SourceSpan));
    }
}
