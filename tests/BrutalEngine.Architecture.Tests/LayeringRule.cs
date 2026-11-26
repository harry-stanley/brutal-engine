using System;
using System.Collections.Generic;
using System.Linq;

namespace BrutalEngine.Architecture.Tests;

/// <summary>
/// docs/03-architecture.md 3.1: dependencies point downwards only. A module may depend on a strictly lower layer, or
/// on a same-layer module when the layer map lists that edge. Backends are referenced only by hosts (they are chosen
/// in the runtime host), and runtime modules never reference tools.
/// </summary>
internal static class LayeringRule
{
    public const string Name = "dependencies point to lower layers only";

    /// <summary>
    /// Checks one module's references against the layer map and returns one message per violation, or an empty list.
    /// An unknown module is itself a violation; otherwise each distinct engine reference (other than the module itself
    /// and Roslyn components) must be a known module that is a tool only when the module is a tool, isn't in a higher
    /// layer, is in the same layer only through a listed edge, and is a backend only when the module is a host or a
    /// tool.
    /// </summary>
    /// <param name="map">The layer map to check against.</param>
    /// <param name="module">The referencing assembly.</param>
    /// <param name="references">Names of the assemblies it references. Non-engine names are ignored.</param>
    /// <param name="source">Where the references came from, for the failure message.</param>
    public static IReadOnlyList<string> Check(LayerMap map, string module, IEnumerable<string> references, string source)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(references);

        if (!map.TryGet(module, out ModuleDefinition from))
        {
            return [$"{module} ({source}): unknown BrutalEngine module. Add it to EngineLayers with its layer from docs/03-architecture.md 3.1."];
        }

        List<string> violations = [];
        foreach (string reference in references.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!LayerMap.IsEngineAssemblyName(reference) || LayerMap.IsRoslynComponentName(reference) || reference == module)
            {
                continue;
            }

            string? why = Why(map, from, reference);
            if (why is not null)
            {
                violations.Add($"{module} -> {reference} ({source}): {why}");
            }
        }

        return violations;
    }

    private static string? Why(LayerMap map, ModuleDefinition from, string reference)
    {
        if (!map.TryGet(reference, out ModuleDefinition to))
        {
            return "unknown BrutalEngine module. Add it to EngineLayers with its layer from docs/03-architecture.md 3.1.";
        }

        if (to.Kind == ModuleKind.Tool && from.Kind != ModuleKind.Tool)
        {
            return "runtime modules never reference tools (docs/03-architecture.md 3.1).";
        }

        if (to.Layer > from.Layer)
        {
            return $"L{from.Layer} depends on higher layer L{to.Layer}. Dependencies point downwards only.";
        }

        if (to.Layer == from.Layer && !map.IsSameLayerEdgeAllowed(from.AssemblyName, to.AssemblyName))
        {
            return $"both are in L{to.Layer}, and this same-layer edge isn't listed in EngineLayers. List it there, with the doc section that justifies it, or move the shared code down a layer.";
        }

        if (to.Kind == ModuleKind.Backend && from.Kind is not (ModuleKind.Host or ModuleKind.Tool))
        {
            return "backends are chosen in the runtime host, so only host modules reference them (docs/03-architecture.md 3.1). Depend on the abstraction instead.";
        }

        return null;
    }
}
