using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace BrutalEngine.Analyzers;

/// <summary>
/// BE1001: reports calls whose status or result value is silently discarded.
/// </summary>
/// <remarks>
/// <para>
/// A call is reported when it is used as a statement (its value is thrown away) and its return type is a
/// <em>status type</em>. The built-in status types are <c>BrutalEngine.Core.Result</c>,
/// <c>BrutalEngine.Core.Result&lt;T&gt;</c> and <c>Brutal.VulkanApi.VkResult</c>. More can be added per
/// project in <c>.editorconfig</c>:
/// <c>brutal_engine.be1001.additional_status_types = Full.Type.Name;Other.Type`1</c> (metadata names).
/// </para>
/// <para>
/// An explicit discard (<c>_ = Call();</c>) is not reported. It's the visible way to say "ignored on purpose",
/// and the coding standards require a comment explaining why.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DiscardedStatusResultAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic ID.</summary>
    public const string DiagnosticId = "BE1001";

    /// <summary>The <c>.editorconfig</c> key listing extra status types (semicolon-separated metadata names).</summary>
    public const string AdditionalTypesOptionKey = "brutal_engine.be1001.additional_status_types";

    private static readonly string[] _builtInStatusTypes =
    [
        "BrutalEngine.Core.Result",
        "BrutalEngine.Core.Result`1",
        "Brutal.VulkanApi.VkResult",
    ];

    private static readonly DiagnosticDescriptor _rule = new(
        id: DiagnosticId,
        title: "Status or result value is discarded",
        messageFormat: "The {0} returned by '{1}' is discarded. Check it, or discard it explicitly with '_ =' and a comment explaining why.",
        category: "Reliability",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Native status codes and engine results report failures. Ignoring them hides errors (docs/07-coding-standards.md, native interop rules).",
        helpLinkUri: "https://github.com/harry-stanley/brutal-engine/blob/main/docs/07-coding-standards.md#analyzer-rules-we-own-brutalengineanalyzers-ids-be1xxx");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [_rule];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        HashSet<INamedTypeSymbol> statusTypes = new(SymbolEqualityComparer.Default);
        foreach (string name in _builtInStatusTypes)
        {
            AddType(context.Compilation, name, statusTypes);
        }

        if (context.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(AdditionalTypesOptionKey, out string? configured))
        {
            foreach (string name in configured.Split([';', ','], StringSplitOptions.RemoveEmptyEntries))
            {
                AddType(context.Compilation, name.Trim(), statusTypes);
            }
        }

        if (statusTypes.Count == 0)
        {
            return;
        }

        context.RegisterOperationAction(ctx => AnalyzeExpressionStatement(ctx, statusTypes), OperationKind.ExpressionStatement);
    }

    private static void AddType(Compilation compilation, string metadataName, HashSet<INamedTypeSymbol> statusTypes)
    {
        if (metadataName.Length > 0 && compilation.GetTypeByMetadataName(metadataName) is { } type)
        {
            statusTypes.Add(type);
        }
    }

    private static void AnalyzeExpressionStatement(OperationAnalysisContext context, HashSet<INamedTypeSymbol> statusTypes)
    {
        var statement = (IExpressionStatementOperation)context.Operation;
        IOperation value = Unwrap(statement.Operation);

        if (value is not IInvocationOperation invocation)
        {
            return;
        }

        ITypeSymbol? type = statement.Operation is IAwaitOperation awaited ? awaited.Type : invocation.Type;
        if (type is INamedTypeSymbol named && statusTypes.Contains(named.OriginalDefinition))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                _rule,
                invocation.Syntax.GetLocation(),
                named.OriginalDefinition.Name,
                invocation.TargetMethod.Name));
        }
    }

    private static IOperation Unwrap(IOperation operation)
    {
        while (true)
        {
            switch (operation)
            {
                case IAwaitOperation awaited:
                    operation = awaited.Operation;
                    break;
                case IConditionalAccessOperation conditional:
                    operation = conditional.WhenNotNull;
                    break;
                default:
                    return operation;
            }
        }
    }
}
