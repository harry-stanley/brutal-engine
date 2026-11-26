namespace BrutalEngine.Architecture.Tests.Fakes;

/// <summary>Project file text in the shape of src/*/*.csproj, for the ship-gate rule's unit tests.</summary>
internal static class ShipGateFixtures
{
    /// <summary>A library project whose first property group holds <paramref name="properties"/>.</summary>
    public static string Library(string properties) =>
        $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <Description>A fixture library.</Description>
            {properties}
          </PropertyGroup>
        </Project>
        """;

    /// <summary>A library project that declares <paramref name="milestone"/> as its <c>BrutalEngineMilestone</c>.</summary>
    public static string WithMilestone(string milestone) =>
        Library($"<BrutalEngineMilestone>{milestone}</BrutalEngineMilestone>");

    /// <summary>A library with a valid milestone that also sets <c>IsPackable</c> itself, in a property group of its own.</summary>
    public const string SettingIsPackable =
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <BrutalEngineMilestone>1</BrutalEngineMilestone>
          </PropertyGroup>
          <PropertyGroup Condition="'$(Configuration)' == 'Release'">
            <IsPackable>false</IsPackable>
          </PropertyGroup>
        </Project>
        """;

    /// <summary>A milestone declared only under a condition, so its value depends on how the project is built.</summary>
    public const string ConditionalMilestone =
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup Condition="'$(Configuration)' == 'Release'">
            <BrutalEngineMilestone>1</BrutalEngineMilestone>
          </PropertyGroup>
        </Project>
        """;

    /// <summary>A milestone element that carries its own condition.</summary>
    public const string ConditionalMilestoneElement =
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <BrutalEngineMilestone Condition="'$(Configuration)' == 'Release'">1</BrutalEngineMilestone>
          </PropertyGroup>
        </Project>
        """;

    /// <summary>The milestone declared twice, in two property groups.</summary>
    public const string TwoMilestones =
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <BrutalEngineMilestone>1</BrutalEngineMilestone>
          </PropertyGroup>
          <PropertyGroup>
            <BrutalEngineMilestone>2</BrutalEngineMilestone>
          </PropertyGroup>
        </Project>
        """;

    /// <summary>Text that isn't XML.</summary>
    public const string NotXml = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>";
}
