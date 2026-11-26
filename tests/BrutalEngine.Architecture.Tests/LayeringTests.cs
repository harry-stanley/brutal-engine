using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace BrutalEngine.Architecture.Tests;

public sealed class LayeringTests
{
    private static readonly LayerMap _map = new(
        [
            LayerMapTests.Module("BrutalEngine.Core", 1),
            LayerMapTests.Module("BrutalEngine.Platform", 2),
            LayerMapTests.Module("BrutalEngine.Platform.Sdl3", 2, ModuleKind.Backend),
            LayerMapTests.Module("BrutalEngine.Graphics", 2),
            LayerMapTests.Module("BrutalEngine.World", 3),
            LayerMapTests.Module("BrutalEngine.Runtime", 5, ModuleKind.Host),
            LayerMapTests.Module("BrutalEngine.Cook", 6, ModuleKind.Tool),
        ],
        [new("BrutalEngine.Platform.Sdl3", "BrutalEngine.Platform")]);

    [Fact]
    public void EngineModules_DependOnlyOnLowerLayers()
    {
        List<string> violations = [];
        foreach (EngineModule module in EngineModules.Load())
        {
            IEnumerable<string?> assemblyReferences = module.Assembly.GetReferencedAssemblies().Select(r => r.Name);
            violations.AddRange(LayeringRule.Check(EngineLayers.Map, module.Name, assemblyReferences.OfType<string>(), "assembly reference"));

            IEnumerable<string> projectReferences = module.Project.ProjectReferences().Select(Path.GetFileNameWithoutExtension).OfType<string>();
            violations.AddRange(LayeringRule.Check(EngineLayers.Map, module.Name, projectReferences, $"ProjectReference in src/{module.Name}/{module.Name}.csproj"));
        }

        Violations.AssertNone(LayeringRule.Name, violations);
    }

    [Theory]
    [InlineData("BrutalEngine.World", "BrutalEngine.Core")]
    [InlineData("BrutalEngine.World", "BrutalEngine.Platform")]
    [InlineData("BrutalEngine.Platform.Sdl3", "BrutalEngine.Platform")]
    [InlineData("BrutalEngine.Runtime", "BrutalEngine.Platform.Sdl3")]
    [InlineData("BrutalEngine.Cook", "BrutalEngine.Runtime")]
    [InlineData("BrutalEngine.Cook", "BrutalEngine.Platform.Sdl3")]
    public void Check_AllowedDependency_ReportsNothing(string module, string reference)
    {
        Assert.Empty(LayeringRule.Check(_map, module, [reference], "test"));
    }

    [Theory]
    [InlineData("BrutalEngine.Core", "BrutalEngine.World", "depends on higher layer L3")]
    [InlineData("BrutalEngine.Platform", "BrutalEngine.Graphics", "this same-layer edge isn't listed")]
    [InlineData("BrutalEngine.Platform", "BrutalEngine.Platform.Sdl3", "this same-layer edge isn't listed")]
    [InlineData("BrutalEngine.World", "BrutalEngine.Platform.Sdl3", "only host modules reference them")]
    [InlineData("BrutalEngine.Runtime", "BrutalEngine.Cook", "never reference tools")]
    [InlineData("BrutalEngine.World", "BrutalEngine.Unknown", "unknown BrutalEngine module")]
    [InlineData("BrutalEngine.World", "BrutalEngine", "unknown BrutalEngine module")]
    public void Check_ForbiddenDependency_IsReportedWithReason(string module, string reference, string reason)
    {
        string violation = Assert.Single(LayeringRule.Check(_map, module, [reference], "test source"));

        Assert.StartsWith($"{module} -> {reference} (test source): ", violation, StringComparison.Ordinal);
        Assert.Contains(reason, violation, StringComparison.Ordinal);
    }

    [Fact]
    public void GraphicsToPlatform_IsAllowedSameLayerEdge()
    {
        Violations.AssertNone(LayeringRule.Name, LayeringRule.Check(EngineLayers.Map, "BrutalEngine.Graphics", ["BrutalEngine.Platform"], "test"));
        Assert.True(EngineLayers.Map.IsSameLayerEdgeAllowed("BrutalEngine.Graphics", "BrutalEngine.Platform"));
    }

    [Theory]
    [InlineData("BrutalEngine.Graphics.Vulkan", "BrutalEngine.Platform")]
    [InlineData("BrutalEngine.Platform", "BrutalEngine.Graphics")]
    public void EngineLayers_GraphicsEdgeOnlyAllowsGraphicsToPlatform(string module, string reference)
    {
        string violation = Assert.Single(LayeringRule.Check(EngineLayers.Map, module, [reference], "test"));

        Assert.Contains("this same-layer edge isn't listed", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_UnknownModule_IsReported()
    {
        string violation = Assert.Single(LayeringRule.Check(_map, "BrutalEngine.Unknown", ["BrutalEngine.Core"], "test"));

        Assert.Contains("unknown BrutalEngine module", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_IgnoresNonEngineRoslynAndSelfReferences()
    {
        string[] references = ["System.Runtime", "Brutal.Core", "Ahwoo.Brutal.Vulkan", "BrutalEngine.Analyzers", "BrutalEngine.Generators", "BrutalEngine.Core"];

        Assert.Empty(LayeringRule.Check(_map, "BrutalEngine.Core", references, "test"));
    }

    [Fact]
    public void Check_ReportsEveryViolation()
    {
        IReadOnlyList<string> violations = LayeringRule.Check(_map, "BrutalEngine.Core", ["BrutalEngine.World", "BrutalEngine.Runtime", "BrutalEngine.World"], "test");

        Assert.Equal(2, violations.Count);
    }
}
