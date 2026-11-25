using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace BrutalEngine.Architecture.Tests;

public sealed partial class LayerMapTests
{
    [Fact]
    public void EngineLayers_IsValid()
    {
        Violations.AssertNone("the layer map is consistent", EngineLayers.Map.Validate());
    }

    [Fact]
    public void EngineLayers_MatchesModulesPlannedInRepoLayout()
    {
        Dictionary<string, string> planned = PlannedModules(Repository.ReadDoc("04-repo-layout.md"));
        List<string> problems = [];

        // Sanity check on the parser, so a reformatted doc can't make this test pass vacuously.
        Assert.Contains("BrutalEngine.Core", planned.Keys);
        Assert.Contains("BrutalEngine.Cook", planned.Keys);

        foreach ((string name, string folder) in planned.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!EngineLayers.Map.TryGet(name, out ModuleDefinition module))
            {
                problems.Add($"{name}: planned under {folder}/ in docs/04-repo-layout.md but missing from EngineLayers.");
            }
            else if ((module.Kind == ModuleKind.Tool) != (folder == "tools"))
            {
                problems.Add($"{name}: planned under {folder}/ but mapped as {module.Kind}; only tools/ modules are tools.");
            }
        }

        foreach (ModuleDefinition module in EngineLayers.Map.Modules.Where(m => !planned.ContainsKey(m.AssemblyName)))
        {
            problems.Add($"{module.AssemblyName}: in EngineLayers but not planned in docs/04-repo-layout.md. Update the doc too.");
        }

        Violations.AssertNone("the layer map covers every planned module", problems);
    }

    [Fact]
    public void Validate_ValidMap_ReportsNothing()
    {
        LayerMap map = new(
            [Module("BrutalEngine.Platform", 2), Module("BrutalEngine.Platform.Sdl3", 2, ModuleKind.Backend)],
            [new("BrutalEngine.Platform.Sdl3", "BrutalEngine.Platform")]);

        Assert.Empty(map.Validate());
    }

    [Theory]
    [InlineData("BrutalEngine.Core", 0)]
    [InlineData("BrutalEngine.Core", 7)]
    [InlineData("Brutal.Core", 1)]
    [InlineData("BrutalEngine.Generators", 1)]
    public void Validate_BadModule_IsReported(string name, int layer)
    {
        LayerMap map = new([Module(name, layer)], []);

        Assert.Single(map.Validate(), p => p.StartsWith(name + ":", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_BackendOutsideL2_IsReported()
    {
        LayerMap map = new(
            [Module("BrutalEngine.Platform", 3), Module("BrutalEngine.Platform.Sdl3", 3, ModuleKind.Backend)],
            [new("BrutalEngine.Platform.Sdl3", "BrutalEngine.Platform")]);

        Assert.Contains(map.Validate(), p => p.Contains("backends live in L2", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_BackendWithoutAbstraction_IsReported()
    {
        LayerMap map = new([Module("BrutalEngine.Platform.Sdl3", 2, ModuleKind.Backend)], []);

        Assert.Contains(map.Validate(), p => p.Contains("exactly one same-layer edge", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ToolOutsideL6_IsReported()
    {
        LayerMap map = new([Module("BrutalEngine.Cook", 5, ModuleKind.Tool)], []);

        Assert.Contains(map.Validate(), p => p.Contains("tools live in L6", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_EdgeAcrossLayers_IsReported()
    {
        LayerMap map = new([Module("BrutalEngine.Core", 1), Module("BrutalEngine.World", 3)], [new("BrutalEngine.World", "BrutalEngine.Core")]);

        Assert.Contains(map.Validate(), p => p.Contains("not within one layer", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_EdgeToUnknownModule_IsReported()
    {
        LayerMap map = new([Module("BrutalEngine.World", 3)], [new("BrutalEngine.World", "BrutalEngine.Nope")]);

        Assert.Contains(map.Validate(), p => p.Contains("both ends must be modules", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_SameLayerCycle_IsReported()
    {
        LayerMap map = new(
            [Module("BrutalEngine.A", 3), Module("BrutalEngine.B", 3), Module("BrutalEngine.C", 3)],
            [new("BrutalEngine.A", "BrutalEngine.B"), new("BrutalEngine.B", "BrutalEngine.C"), new("BrutalEngine.C", "BrutalEngine.A")]);

        string problem = Assert.Single(map.Validate());
        Assert.Contains("cycle: BrutalEngine.A -> BrutalEngine.B -> BrutalEngine.C -> BrutalEngine.A", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_DuplicateModule_Throws()
    {
        Assert.Throws<ArgumentException>(() => new LayerMap([Module("BrutalEngine.Core", 1), Module("BrutalEngine.Core", 2)], []));
    }

    [Theory]
    [InlineData("BrutalEngine", true)]
    [InlineData("BrutalEngine.Core", true)]
    [InlineData("BrutalEngineX", false)]
    [InlineData("Brutal.Core", false)]
    [InlineData(null, false)]
    public void IsEngineAssemblyName_MatchesBrutalEngineNamesOnly(string? name, bool expected)
    {
        Assert.Equal(expected, LayerMap.IsEngineAssemblyName(name));
    }

    internal static ModuleDefinition Module(string name, int layer, ModuleKind kind = ModuleKind.Library) => new(name, layer, kind);

    /// <summary>Module folders listed under src/ and tools/ in the repo-layout tree, keyed by name.</summary>
    private static Dictionary<string, string> PlannedModules(string doc)
    {
        Dictionary<string, string> modules = new(StringComparer.Ordinal);
        string? folder = null;
        foreach (string line in doc.Split('\n'))
        {
            Match top = TopLevelFolder().Match(line);
            if (top.Success)
            {
                folder = top.Groups[1].Value;
                continue;
            }

            Match child = ModuleFolder().Match(line);
            if (child.Success && folder is "src" or "tools" && !LayerMap.IsRoslynComponentName(child.Groups[1].Value))
            {
                modules.Add(child.Groups[1].Value, folder);
            }
        }

        return modules;
    }

    [GeneratedRegex(@"^├── ([A-Za-z0-9._-]+)/")]
    private static partial Regex TopLevelFolder();

    [GeneratedRegex(@"^│   [├└]── (BrutalEngine(?:\.[A-Za-z0-9]+)*)/")]
    private static partial Regex ModuleFolder();
}
