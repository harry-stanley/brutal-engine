using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace BrutalEngine.Architecture.Tests;

/// <summary>An engine library: its project, its entry in the layer map and its loaded assembly.</summary>
internal sealed record EngineModule(ProjectFile Project, ModuleDefinition Definition, Assembly Assembly)
{
    public string Name => Project.Name;
}

internal static class EngineModules
{
    /// <summary>Loads every engine library under src/. ModuleCoverageTests explains any failure here.</summary>
    public static IReadOnlyList<EngineModule> Load()
    {
        List<EngineModule> modules = [];
        foreach (ProjectFile project in Repository.EngineSourceProjects())
        {
            if (!EngineLayers.Map.TryGet(project.Name, out ModuleDefinition definition))
            {
                throw new InvalidOperationException($"{project.Name} is not in the layer map (see ModuleCoverageTests).");
            }

            modules.Add(new EngineModule(project, definition, LoadAssembly(project.Name)));
        }

        return modules;
    }

    public static Assembly LoadAssembly(string name)
    {
        try
        {
            return Assembly.Load(new AssemblyName(name));
        }
        catch (FileNotFoundException exception)
        {
            throw new InvalidOperationException(
                $"Assembly {name} isn't in the architecture test output. Reference src/{name}/{name}.csproj from {Repository.ArchitectureTestProject().Name}.",
                exception);
        }
    }
}

internal static class Violations
{
    /// <summary>Fails with every violation listed, one per line, so a single run shows all of them.</summary>
    public static void AssertNone(string rule, IReadOnlyCollection<string> violations)
    {
        if (violations.Count == 0)
        {
            return;
        }

        Assert.Fail($"{violations.Count} violation(s) of \"{rule}\":\n" + string.Join("\n", violations.Select(v => "  - " + v)));
    }
}
