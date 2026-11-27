using System;
using System.IO;
using System.Linq;
using BrutalEngine.Cook.Shaders;
using Xunit;

namespace BrutalEngine.Cook.Tests.Shaders;

/// <summary>The <c>shader</c> verb end to end, with the real Slang compiler unless a row says otherwise.</summary>
[Collection(SlangSerial.Name)]
public sealed class ShaderCommandTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();

    public void Dispose()
    {
        _temp.Dispose();
        _stdout.Dispose();
        _stderr.Dispose();
    }

    [Fact]
    public void Run_TwoEntries_WritesTwoSpirvFiles()
    {
        string output = _temp.Combine("out");

        CookExitCode exit = CookTriangle(output);

        Assert.Equal(CookExitCode.Success, exit);
        Assert.Equal(
            ["triangle.fragMain.spv", "triangle.vertMain.spv"],
            Directory.GetFiles(output).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        byte[] vertex = File.ReadAllBytes(Path.Combine(output, "triangle.vertMain.spv"));
        byte[] fragment = File.ReadAllBytes(Path.Combine(output, "triangle.fragMain.spv"));
        Assert.Equal(SpirV.Magic, SpirV.FirstWord(vertex));
        Assert.Equal(SpirV.Magic, SpirV.FirstWord(fragment));
        Assert.Equal(SpirV.Version1Dot6, SpirV.Version(vertex));
        Assert.Equal(SpirV.Version1Dot6, SpirV.Version(fragment));
        Assert.Equal(0u, SpirV.EntryPointExecutionModel(vertex));
        Assert.Equal(4u, SpirV.EntryPointExecutionModel(fragment));
        Assert.Equal(
            $"{Path.Combine(output, "triangle.vertMain.spv")}{Environment.NewLine}{Path.Combine(output, "triangle.fragMain.spv")}{Environment.NewLine}",
            _stdout.ToString());
        Assert.Empty(_stderr.ToString());
    }

    [Fact]
    public void Run_ComputeEntry_WritesGlComputeModule()
    {
        string output = _temp.Combine("out");

        CookExitCode exit = ShaderCommand.Run([Fixture.Path("compute.slang"), "--entry", "csMain:compute", "--output-dir", output], _stdout, _stderr);

        Assert.Equal(CookExitCode.Success, exit);
        byte[] module = File.ReadAllBytes(Path.Combine(output, "compute.csMain.spv"));
        Assert.Equal(SpirV.Magic, SpirV.FirstWord(module));
        Assert.Equal(SpirV.Version1Dot6, SpirV.Version(module));
        Assert.Equal(5u, SpirV.EntryPointExecutionModel(module));
    }

    [Fact]
    public void Run_Twice_IsByteIdentical()
    {
        string first = _temp.Combine("first");
        string second = _temp.Combine("second");

        Assert.Equal(CookExitCode.Success, CookTriangle(first));
        Assert.Equal(CookExitCode.Success, CookTriangle(second));

        foreach (string name in (string[])["triangle.vertMain.spv", "triangle.fragMain.spv"])
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(first, name)), File.ReadAllBytes(Path.Combine(second, name)));
        }
    }

    [Fact]
    public void Run_ExistingOutput_IsOverwritten()
    {
        string fresh = _temp.Combine("fresh");
        string stale = _temp.Combine("stale");
        Directory.CreateDirectory(stale);
        File.WriteAllBytes(Path.Combine(stale, "triangle.vertMain.spv"), new byte[64 * 1024]);

        Assert.Equal(CookExitCode.Success, CookTriangle(fresh));
        Assert.Equal(CookExitCode.Success, CookTriangle(stale));

        Assert.Equal(
            File.ReadAllBytes(Path.Combine(fresh, "triangle.vertMain.spv")),
            File.ReadAllBytes(Path.Combine(stale, "triangle.vertMain.spv")));
    }

    [Fact]
    public void Run_MatrixLayout_ReachesSlang()
    {
        string byDefault = _temp.Combine("default");
        string columnMajor = _temp.Combine("column");
        string rowMajor = _temp.Combine("row");

        Assert.Equal(CookExitCode.Success, CookTriangle(byDefault));
        Assert.Equal(CookExitCode.Success, CookTriangle(columnMajor, "--matrix-layout", "column-major"));
        Assert.Equal(CookExitCode.Success, CookTriangle(rowMajor, "--matrix-layout", "row-major"));

        // The vertex shader's push constants hold a matrix, so the layout changes its decorations.
        byte[] defaultBytes = File.ReadAllBytes(Path.Combine(byDefault, "triangle.vertMain.spv"));
        Assert.Equal(defaultBytes, File.ReadAllBytes(Path.Combine(columnMajor, "triangle.vertMain.spv")));
        Assert.NotEqual(defaultBytes, File.ReadAllBytes(Path.Combine(rowMajor, "triangle.vertMain.spv")));
    }

    [Fact]
    public void Run_SyntaxError_ReturnsCompileFailedAndKeepsOutputs()
    {
        string source = Fixture.Path("syntax-error.slang");
        string existing = _temp.Combine("existing");
        string missing = _temp.Combine("missing");
        Directory.CreateDirectory(existing);
        string kept = Path.Combine(existing, "syntax-error.vertMain.spv");
        File.WriteAllBytes(kept, [1, 2, 3]);

        CookExitCode intoExisting = ShaderCommand.Run(
            [source, "--entry", "vertMain:vertex", "--entry", "fragMain:fragment", "--output-dir", existing], _stdout, _stderr);
        CookExitCode intoMissing = ShaderCommand.Run(
            [source, "--entry", "vertMain:vertex", "--entry", "fragMain:fragment", "--output-dir", missing], _stdout, _stderr);

        Assert.Equal(CookExitCode.CompileFailed, intoExisting);
        Assert.Equal(CookExitCode.CompileFailed, intoMissing);
        Assert.Equal(2, (int)intoExisting);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(kept));
        Assert.Equal([kept], Directory.GetFiles(existing));
        Assert.False(Directory.Exists(missing));
        string diagnostics = _stderr.ToString();
        Assert.Contains("error[E", diagnostics, StringComparison.Ordinal);
        Assert.Contains($"--> {source}:", diagnostics, StringComparison.Ordinal);
        Assert.Empty(_stdout.ToString());
    }

    [Fact]
    public void Run_EntryNotInSource_ReturnsCompileFailed()
    {
        string output = _temp.Combine("out");

        CookExitCode exit = CookTriangle(output, "--entry", "noSuchEntry:fragment");

        Assert.Equal(CookExitCode.CompileFailed, exit);
        Assert.Contains("noSuchEntry", _stderr.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData("missing.slang")]
    [InlineData("a-directory.slang")]
    public void Run_MissingSource_ReturnsInputNotFound(string name)
    {
        Directory.CreateDirectory(_temp.Combine("a-directory.slang"));
        string source = _temp.Combine(name);
        string output = _temp.Combine("out");
        var runtime = new CountingSlangRuntime();

        CookExitCode exit = ShaderCommand.Run([source, "--entry", "vertMain:vertex", "--output-dir", output], _stdout, _stderr, runtime);

        Assert.Equal(CookExitCode.InputNotFound, exit);
        Assert.Equal(3, (int)exit);
        Assert.Contains($"source file '{source}' not found", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, runtime.Created);
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData("output directory is a file")]
    [InlineData("output directory's parent is a file")]
    [InlineData("output file is a directory")]
    public void Run_UnwritableOutput_ReturnsOutputFailed(string layout)
    {
        string blocker = _temp.Combine("blocker");
        string output = layout switch
        {
            "output directory is a file" => blocker,
            "output directory's parent is a file" => Path.Combine(blocker, "out"),
            _ => _temp.Combine("out"),
        };
        if (layout == "output file is a directory")
        {
            Directory.CreateDirectory(Path.Combine(output, "triangle.vertMain.spv"));
        }
        else
        {
            File.WriteAllBytes(blocker, [7]);
        }

        CookExitCode exit = CookTriangle(output);

        Assert.Equal(CookExitCode.OutputFailed, exit);
        Assert.Equal(4, (int)exit);
        Assert.Contains($"can't write to '{output}'", _stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_SlangFailsToLoad_ReturnsToolFailure()
    {
        string output = _temp.Combine("out");
        var runtime = new CountingSlangRuntime { CreateThrows = new DllNotFoundException("libslang.so: cannot open shared object file") };

        CookExitCode exit = ShaderCommand.Run(
            [Fixture.Path("triangle.slang"), "--entry", "vertMain:vertex", "--output-dir", output], _stdout, _stderr, runtime);

        Assert.Equal(CookExitCode.ToolFailure, exit);
        Assert.Equal(5, (int)exit);
        Assert.Contains("the Slang runtime failed to load: libslang.so", _stderr.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Run_Help_ReturnsSuccessWithoutSlang()
    {
        var runtime = new CountingSlangRuntime();

        CookExitCode exit = ShaderCommand.Run(["missing.slang", "--help"], _stdout, _stderr, runtime);

        Assert.Equal(CookExitCode.Success, exit);
        Assert.Contains("Usage: brutal-cook shader <source.slang>", _stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("64 bad command line", _stdout.ToString(), StringComparison.Ordinal);
        Assert.Empty(_stderr.ToString());
        Assert.Equal(0, runtime.Created);
    }

    [Fact]
    public void Run_UsageError_PrintsErrorAndUsageToStandardError()
    {
        var runtime = new CountingSlangRuntime();

        CookExitCode exit = ShaderCommand.Run([Fixture.Path("triangle.slang"), "--entry", "vertMain:Vertex", "--output-dir", "out"], _stdout, _stderr, runtime);

        Assert.Equal(CookExitCode.Usage, exit);
        Assert.StartsWith("brutal-cook shader: --entry 'vertMain:Vertex': expected", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage: brutal-cook shader <source.slang>", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(_stdout.ToString());
        Assert.Equal(0, runtime.Created);
    }

    private CookExitCode CookTriangle(string output, params string[] extra) =>
        ShaderCommand.Run(
            [Fixture.Path("triangle.slang"), "--entry", "vertMain:vertex", "--entry", "fragMain:fragment", "--output-dir", output, .. extra],
            _stdout,
            _stderr);
}
