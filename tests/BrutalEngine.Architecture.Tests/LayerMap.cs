using System;
using System.Collections.Generic;
using System.Linq;

namespace BrutalEngine.Architecture.Tests;

/// <summary>What a module is allowed to do beyond the plain layering rule.</summary>
internal enum ModuleKind
{
    /// <summary>An ordinary engine library: an abstraction, a service or a gameplay feature.</summary>
    Library,

    /// <summary>An L2 backend: implements an abstraction and may expose native binding types.</summary>
    Backend,

    /// <summary>A module that picks and wires up backends, so it may reference backend assemblies.</summary>
    Host,

    /// <summary>An L6 tool (cooker, importers, editor). Runtime modules never reference tools.</summary>
    Tool,
}

/// <summary>One planned or existing module: its assembly name (= project = package ID), layer and kind.</summary>
internal sealed record ModuleDefinition(string AssemblyName, int Layer, ModuleKind Kind);

/// <summary>A dependency between two modules, named by assembly.</summary>
internal readonly record struct ModuleEdge(string From, string To)
{
    public override string ToString() => $"{From} -> {To}";
}

/// <summary>
/// The layer map from docs/03-architecture.md 3.1: every module's layer, plus the same-layer dependencies that
/// are allowed explicitly. Everything else must point to a strictly lower layer.
/// </summary>
internal sealed class LayerMap
{
    public const int LowestEngineLayer = 1;
    public const int HighestLayer = 6;

    private readonly Dictionary<string, ModuleDefinition> _modules;
    private readonly HashSet<ModuleEdge> _sameLayerEdges;

    public LayerMap(IEnumerable<ModuleDefinition> modules, IEnumerable<ModuleEdge> sameLayerEdges)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(sameLayerEdges);

        _modules = new Dictionary<string, ModuleDefinition>(StringComparer.Ordinal);
        foreach (ModuleDefinition module in modules)
        {
            if (!_modules.TryAdd(module.AssemblyName, module))
            {
                throw new ArgumentException($"Module {module.AssemblyName} is listed twice.", nameof(modules));
            }
        }

        _sameLayerEdges = [.. sameLayerEdges];
    }

    public IReadOnlyCollection<ModuleDefinition> Modules => _modules.Values;

    public IReadOnlyCollection<ModuleEdge> SameLayerEdges => _sameLayerEdges;

    /// <summary>True for the assembly names the map governs: <c>BrutalEngine</c> and <c>BrutalEngine.*</c>.</summary>
    public static bool IsEngineAssemblyName(string? name) =>
        name is not null && (name == "BrutalEngine" || name.StartsWith("BrutalEngine.", StringComparison.Ordinal));

    /// <summary>
    /// True for Roslyn components (analyzers, source generators). They are build-time only, never referenced at
    /// runtime, and recognised by name exactly as src/Directory.Build.props does.
    /// </summary>
    public static bool IsRoslynComponentName(string name) =>
        name.EndsWith(".Analyzers", StringComparison.Ordinal) || name.EndsWith(".Generators", StringComparison.Ordinal);

    public bool TryGet(string assemblyName, out ModuleDefinition module) =>
        _modules.TryGetValue(assemblyName, out module!);

    public bool IsSameLayerEdgeAllowed(string from, string to) => _sameLayerEdges.Contains(new ModuleEdge(from, to));

    /// <summary>Checks the map itself, so a bad edit to it fails with a clear message.</summary>
    public IReadOnlyList<string> Validate()
    {
        List<string> problems = [];

        foreach (ModuleDefinition module in _modules.Values.OrderBy(m => m.AssemblyName, StringComparer.Ordinal))
        {
            if (!IsEngineAssemblyName(module.AssemblyName) || IsRoslynComponentName(module.AssemblyName))
            {
                problems.Add($"{module.AssemblyName}: not a BrutalEngine runtime or tool module name.");
            }

            if (module.Layer is < LowestEngineLayer or > HighestLayer)
            {
                problems.Add($"{module.AssemblyName}: layer L{module.Layer} is outside L{LowestEngineLayer}-L{HighestLayer}.");
            }

            if (module.Kind == ModuleKind.Backend && module.Layer != 2)
            {
                problems.Add($"{module.AssemblyName}: backends live in L2, not L{module.Layer}.");
            }

            if (module.Kind == ModuleKind.Tool && module.Layer != HighestLayer)
            {
                problems.Add($"{module.AssemblyName}: tools live in L{HighestLayer}, not L{module.Layer}.");
            }

            if (module.Kind == ModuleKind.Backend)
            {
                int abstractions = _sameLayerEdges.Count(e =>
                    e.From == module.AssemblyName && TryGet(e.To, out ModuleDefinition to) && to.Kind == ModuleKind.Library);
                if (abstractions != 1)
                {
                    problems.Add($"{module.AssemblyName}: a backend needs exactly one same-layer edge to the abstraction it implements, found {abstractions}.");
                }
            }
        }

        foreach (ModuleEdge edge in _sameLayerEdges.OrderBy(e => e.ToString(), StringComparer.Ordinal))
        {
            bool hasFrom = TryGet(edge.From, out ModuleDefinition from);
            bool hasTo = TryGet(edge.To, out ModuleDefinition to);
            if (!hasFrom || !hasTo)
            {
                problems.Add($"Same-layer edge {edge}: both ends must be modules in the map.");
            }
            else if (from.Layer != to.Layer)
            {
                problems.Add($"Same-layer edge {edge}: L{from.Layer} -> L{to.Layer} is not within one layer.");
            }
            else if (edge.From == edge.To)
            {
                problems.Add($"Same-layer edge {edge}: a module can't depend on itself.");
            }
        }

        foreach (string cycle in FindSameLayerCycles())
        {
            problems.Add($"Same-layer edges form a cycle: {cycle}.");
        }

        return problems;
    }

    private List<string> FindSameLayerCycles()
    {
        var outgoing = _sameLayerEdges
            .Where(e => e.From != e.To)
            .GroupBy(e => e.From, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.To).Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        HashSet<string> done = new(StringComparer.Ordinal);
        List<string> path = [];
        List<string> cycles = [];

        foreach (string start in outgoing.Keys.Order(StringComparer.Ordinal))
        {
            Visit(start);
        }

        return cycles;

        void Visit(string node)
        {
            int onPath = path.IndexOf(node);
            if (onPath >= 0)
            {
                cycles.Add(string.Join(" -> ", path.Skip(onPath).Append(node)));
                return;
            }

            if (!done.Add(node))
            {
                return;
            }

            path.Add(node);
            foreach (string next in outgoing.GetValueOrDefault(node) ?? [])
            {
                Visit(next);
            }

            path.RemoveAt(path.Count - 1);
        }
    }
}
