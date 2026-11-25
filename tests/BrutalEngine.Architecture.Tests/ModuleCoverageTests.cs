using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace BrutalEngine.Architecture.Tests;

/// <summary>New modules can't escape the architecture rules: every engine library must be mapped and loaded.</summary>
public sealed class ModuleCoverageTests
{
    [Fact]
    public void EngineSourceProjects_IncludeCore()
    {
        IReadOnlyList<ProjectFile> projects = Repository.EngineSourceProjects();

        // Guards against a discovery bug making every other test pass vacuously.
        Assert.Contains(projects, p => p.Name == "BrutalEngine.Core");
        Assert.DoesNotContain(projects, p => LayerMap.IsRoslynComponentName(p.Name));
    }

    [Fact]
    public void EveryEngineProject_IsReferencedByArchitectureTests()
    {
        ProjectFile tests = Repository.ArchitectureTestProject();
        HashSet<string> referenced = new(tests.ProjectReferences(), StringComparer.OrdinalIgnoreCase);

        string[] missing =
        [
            .. Repository.EngineSourceProjects()
                .Where(p => !referenced.Contains(p.Path))
                .Select(p => $"{p.Name}: add <ProjectReference Include=\"../../src/{p.Name}/{p.Name}.csproj\" /> to {tests.Name}.csproj so the architecture rules check it."),
        ];

        Violations.AssertNone("every engine library is checked by the architecture tests", missing);
    }

    [Fact]
    public void EveryEngineProject_IsInLayerMap()
    {
        List<string> problems = [];
        foreach (ProjectFile project in Repository.EngineSourceProjects())
        {
            if (!EngineLayers.Map.TryGet(project.Name, out ModuleDefinition definition))
            {
                problems.Add($"{project.Name}: unknown module. Add it to EngineLayers with its layer from docs/03-architecture.md 3.1.");
            }
            else if (definition.Kind == ModuleKind.Tool)
            {
                problems.Add($"{project.Name}: mapped as a tool, but tools live under tools/, not src/.");
            }
        }

        Violations.AssertNone("every engine library has a layer", problems);
    }

    [Fact]
    public void EveryEngineProject_LoadsUnderItsProjectName()
    {
        foreach (EngineModule module in EngineModules.Load())
        {
            Assert.Equal(module.Name, module.Assembly.GetName().Name);
        }
    }
}
