using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace BrutalEngine.Architecture.Tests;

/// <summary>
/// ADR-0011: the smoke harness's ObjectPool probe expects the version that Directory.Packages.props pins. The probe
/// compares the loaded assembly with a literal of its own, so a pin that moved without the literal would otherwise
/// fail only when the smoke harness runs. This test reads both files as text and names them when they differ.
/// </summary>
public sealed partial class ProbeVersionTests
{
    private const string Package = "Microsoft.Extensions.ObjectPool";
    private const string Constant = "ExpectedPackageVersion";

    private static readonly string _probePath = FixturePath("tests", "Fixture.Smoke", "ObjectPoolProbe.cs");
    private static readonly string _propsPath = FixturePath("Directory.Packages.props");

    [Fact]
    public void SmokeProbeExpectedVersion_MatchesCentralPin()
    {
        string probe = Path.Combine(Repository.Root(), "tests", "BrutalEngine.Aot.Smoke", "ObjectPoolProbe.cs");
        string props = Path.Combine(Repository.Root(), "Directory.Packages.props");

        string? problem = Compare(probe, File.ReadAllText(probe), props, File.ReadAllText(props));

        Assert.True(problem is null, problem);
    }

    [Fact]
    public void Compare_MismatchedFixture_FailsNamingBothFiles()
    {
        string? problem = Compare(_probePath, Probe(Literal("11.0.0")), _propsPath, Props(Pin("11.0.1")));

        Assert.NotNull(problem);
        Assert.Contains($"{_probePath} expects {Package} 11.0.0", problem, StringComparison.Ordinal);
        Assert.Contains($"{_propsPath} pins 11.0.1", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""private const string ExpectedPackageVersion = "11.0.0";""", """<PackageVersion Include="Microsoft.Extensions.ObjectPool" Version="11.0.0" />""")]
    [InlineData("""private const string ExpectedPackageVersion="12.1.3-rc.1";""", """<PackageVersion Include="Microsoft.Extensions.ObjectPool" Version="12.1.3-rc.1" />""")]
    [InlineData("""public const string ExpectedPackageVersion = "11.0.0";""", """<packageversion Include="microsoft.extensions.objectpool" Version="11.0.0" />""")]
    public void Compare_MatchingFixture_ReportsNothing(string declaration, string pin)
    {
        // NuGet package IDs and MSBuild item names are case-insensitive.
        Assert.Null(Compare(_probePath, Probe(declaration), _propsPath, Props(pin)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("private const string ExpectedPackageVersion = Versions.ObjectPool;")]
    [InlineData("""private const string ExpectedPackageVersion = "";""")]
    [InlineData("""private const string ExpectedVersion = "11.0.0";""")]
    [InlineData("""private const string ExpectedPackageVersion = "11.0.0"; private const string ExpectedPackageVersion = "11.0.0";""")]
    public void Compare_ProbeWithoutOneVersionLiteral_NamesTheProbeFile(string declaration)
    {
        string? problem = Compare(_probePath, Probe(declaration), _propsPath, Props(Pin("11.0.0")));

        Assert.NotNull(problem);
        Assert.StartsWith($"{_probePath} ", problem, StringComparison.Ordinal);
        Assert.Contains(Constant, problem, StringComparison.Ordinal);
        Assert.DoesNotContain(_propsPath, problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<Project><ItemGroup><PackageVersion Include="MinVer" Version="11.0.0" /></ItemGroup></Project>""")]
    [InlineData("""<Project><ItemGroup><PackageVersion Include="Microsoft.Extensions.ObjectPool" /></ItemGroup></Project>""")]
    [InlineData("""<Project><ItemGroup><PackageVersion Include="Microsoft.Extensions.ObjectPool" Version="" /></ItemGroup></Project>""")]
    [InlineData("""<Project><ItemGroup><PackageVersion Include="Microsoft.Extensions.ObjectPool" Version="11.0.0" /><PackageVersion Include="Microsoft.Extensions.ObjectPool" Version="11.0.0" /></ItemGroup></Project>""")]
    [InlineData("<Project><ItemGroup>")]
    public void Compare_PropsWithoutOnePin_NamesThePropsFile(string props)
    {
        string? problem = Compare(_probePath, Probe(Literal("11.0.0")), _propsPath, props);

        Assert.NotNull(problem);
        Assert.StartsWith($"{_propsPath} ", problem, StringComparison.Ordinal);
        Assert.Contains(Package, problem, StringComparison.Ordinal);
        Assert.DoesNotContain(_probePath, problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns null when the probe's <c>ExpectedPackageVersion</c> literal equals the central pin, or the reason they
    /// differ or can't be read, naming the file to fix.
    /// </summary>
    private static string? Compare(string probePath, string probeText, string propsPath, string propsText)
    {
        MatchCollection literals = ExpectedVersionPattern().Matches(probeText);
        if (literals.Count != 1)
        {
            return $"{probePath} declares {Constant} as a non-empty string literal {literals.Count} times, so the version the "
                + $"smoke probe expects can't be read. Declare it once, as `const string {Constant} = \"<version>\";`.";
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(propsText);
        }
        catch (XmlException exception)
        {
            return $"{propsPath} isn't a readable MSBuild file ({exception.Message}), so its {Package} pin can't be read.";
        }

        string?[] pins =
        [
            .. document.Descendants()
                .Where(e => string.Equals(e.Name.LocalName, "PackageVersion", StringComparison.OrdinalIgnoreCase)
                    && string.Equals((string?)e.Attribute("Include"), Package, StringComparison.OrdinalIgnoreCase))
                .Select(e => (string?)e.Attribute("Version")),
        ];
        if (pins.Length != 1)
        {
            return $"{propsPath} has {pins.Length} PackageVersion items for {Package}, so the central pin can't be read. Pin it "
                + "once; see ADR-0011.";
        }

        if (string.IsNullOrEmpty(pins[0]))
        {
            return $"{propsPath} has a PackageVersion item for {Package} without a Version attribute, so the central pin can't "
                + "be read.";
        }

        string expected = literals[0].Groups[1].Value;
        return expected == pins[0]
            ? null
            : $"{probePath} expects {Package} {expected} ({Constant}), but {propsPath} pins {pins[0]}. The smoke probe fails "
                + "unless they are equal: change both in the same commit.";
    }

    [GeneratedRegex("""\bconst\s+string\s+ExpectedPackageVersion\s*=\s*"([^"]+)"\s*;""", RegexOptions.CultureInvariant)]
    private static partial Regex ExpectedVersionPattern();

    private static string Literal(string version) => $"private const string {Constant} = \"{version}\";";

    private static string Pin(string version) => $"<PackageVersion Include=\"{Package}\" Version=\"{version}\" />";

    /// <summary>Source text in the shape of the probe: a class whose constants include <paramref name="declaration"/>.</summary>
    private static string Probe(string declaration) =>
        $$"""
        internal static class ObjectPoolProbe
        {
            private const string ExpectedAssemblyName = "Microsoft.Extensions.ObjectPool";
            {{declaration}}
        }
        """;

    /// <summary>Text in the shape of Directory.Packages.props, with another package's pin beside <paramref name="pin"/>.</summary>
    private static string Props(string pin) =>
        $"""
        <Project>
          <ItemGroup>
            <PackageVersion Include="MinVer" Version="8.0.0" />
            {pin}
          </ItemGroup>
        </Project>
        """;

    private static string FixturePath(params string[] parts) =>
        Path.Combine([Path.GetPathRoot(Repository.Root())!, "repo", .. parts]);
}
