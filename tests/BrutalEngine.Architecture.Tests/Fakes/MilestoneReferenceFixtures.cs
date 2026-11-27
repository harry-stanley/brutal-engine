using System.Linq;

namespace BrutalEngine.Architecture.Tests.Fakes;

/// <summary>
/// Project file text in the shape of src/*/*.csproj, with a milestone and ProjectReference items, for the
/// milestone-reference rule's unit tests.
/// </summary>
internal static class MilestoneReferenceFixtures
{
    /// <summary>
    /// A library that declares <paramref name="milestone"/> as its <c>BrutalEngineMilestone</c> and references each
    /// project in <paramref name="references"/> by the path a sibling folder under src/ has.
    /// </summary>
    public static string Library(string milestone, params string[] references) =>
        WithProperties($"<BrutalEngineMilestone>{milestone}</BrutalEngineMilestone>", references);

    /// <summary>
    /// A library whose property group holds <paramref name="properties"/>, and that references each project in
    /// <paramref name="references"/> by the path a sibling folder under src/ has.
    /// </summary>
    public static string WithProperties(string properties, params string[] references) =>
        WithItems(properties, [.. references.Select(name => $"<ProjectReference Include=\"../{name}/{name}.csproj\" />")]);

    /// <summary>A library of milestone 1 whose item group holds <paramref name="items"/> as written.</summary>
    public static string Milestone1WithItems(params string[] items) =>
        WithItems("<BrutalEngineMilestone>1</BrutalEngineMilestone>", items);

    private static string WithItems(string properties, string[] items) =>
        $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <Description>A fixture library.</Description>
            {properties}
          </PropertyGroup>
          <ItemGroup>
            {string.Join("\n    ", items)}
          </ItemGroup>
        </Project>
        """;
}
