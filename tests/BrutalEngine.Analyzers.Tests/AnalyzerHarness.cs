using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BrutalEngine.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace BrutalEngine.Analyzers.Tests;

/// <summary>Compiles a snippet and runs one analyzer over it.</summary>
internal static class AnalyzerHarness
{
    private static readonly ImmutableArray<MetadataReference> _references = CreateReferences();

    public static async Task<ImmutableArray<Diagnostic>> RunAsync(
        DiagnosticAnalyzer analyzer,
        string source,
        IReadOnlyDictionary<string, string>? globalOptions,
        CancellationToken cancellationToken)
    {
        var compilation = CSharpCompilation.Create(
            "AnalyzerTestInput",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), cancellationToken: cancellationToken)],
            _references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        ImmutableArray<Diagnostic> compileErrors = [.. compilation.GetDiagnostics(cancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error)];
        if (!compileErrors.IsEmpty)
        {
            throw new InvalidOperationException("Test input does not compile:\n" + string.Join("\n", compileErrors));
        }

        AnalyzerOptions options = new([], new GlobalOptionsProvider(globalOptions ?? new Dictionary<string, string>()));
        CompilationWithAnalyzers withAnalyzers = compilation.WithAnalyzers([analyzer], options);
        ImmutableArray<Diagnostic> diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        return [.. diagnostics.OrderBy(d => d.Location.SourceSpan.Start)];
    }

    private static ImmutableArray<MetadataReference> CreateReferences()
    {
        string trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        IEnumerable<MetadataReference> framework = trusted
            .Split(Path.PathSeparator)
            .Where(path => Path.GetFileName(path).StartsWith("System.", StringComparison.Ordinal)
                || Path.GetFileName(path) is "mscorlib.dll" or "netstandard.dll")
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));

        return [.. framework, MetadataReference.CreateFromFile(typeof(Result).Assembly.Location)];
    }

    private sealed class GlobalOptionsProvider(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptionsProvider
    {
        private readonly Options _options = new(values);

        public override AnalyzerConfigOptions GlobalOptions => _options;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _options;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _options;

        private sealed class Options(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value) =>
                values.TryGetValue(key, out value);
        }
    }
}
