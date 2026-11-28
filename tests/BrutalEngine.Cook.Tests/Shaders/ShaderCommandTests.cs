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

        CookExitCode exit = ShaderCommand.Run([Fixture.Shader("compute.slang"), "--entry", "csMain:compute", "--output-dir", output], _stdout, _stderr);

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
            [Fixture.Shader("triangle.slang"), "--entry", "vertMain:vertex", "--output-dir", output], _stdout, _stderr, runtime);

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

        CookExitCode exit = ShaderCommand.Run([Fixture.Shader("triangle.slang"), "--entry", "vertMain:Vertex", "--output-dir", "out"], _stdout, _stderr, runtime);

        Assert.Equal(CookExitCode.Usage, exit);
        Assert.StartsWith("brutal-cook shader: --entry 'vertMain:Vertex': expected", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage: brutal-cook shader <source.slang>", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(_stdout.ToString());
        Assert.Equal(0, runtime.Created);
    }

    [Fact]
    public void Manifest_CooksEveryShaderNextToItsSource()
    {
        string manifest = Fixture.CopyShaders(_temp.Combine("shaders"), withSpirv: false);
        string directory = Path.GetDirectoryName(manifest)!;

        CookExitCode exit = ShaderCommand.Run(["--manifest", manifest], _stdout, _stderr);

        Assert.Equal(CookExitCode.Success, exit);
        Assert.Equal(Fixture.SpirvFiles, Directory.GetFiles(directory, "*.spv").Select(Path.GetFileName).Order(StringComparer.Ordinal));
        foreach ((string name, uint model) in (ValueTuple<string, uint>[])[("triangle.vertMain.spv", 0), ("triangle.fragMain.spv", 4), ("compute.csMain.spv", 5)])
        {
            byte[] cooked = File.ReadAllBytes(Path.Combine(directory, name));
            Assert.Equal(SpirV.Version1Dot6, SpirV.Version(cooked));
            Assert.Equal(model, SpirV.EntryPointExecutionModel(cooked));
            Assert.Equal(File.ReadAllBytes(Fixture.Shader(name)), cooked);
        }

        Assert.Equal(Lines(Path.Combine(directory, "triangle.vertMain.spv"), Path.Combine(directory, "triangle.fragMain.spv"), Path.Combine(directory, "compute.csMain.spv")), _stdout.ToString());
        Assert.Empty(_stderr.ToString());
    }

    [Fact]
    public void Manifest_TwoManifests_CooksBoth()
    {
        string first = Fixture.CopyShaders(_temp.Combine("first"), withSpirv: false);
        string second = Fixture.CopyShaders(_temp.Combine("second"), withSpirv: false);

        CookExitCode exit = ShaderCommand.Run(["--manifest", first, "--manifest", second], _stdout, _stderr);

        Assert.Equal(CookExitCode.Success, exit);
        Assert.Equal(3, Directory.GetFiles(Path.GetDirectoryName(first)!, "*.spv").Length);
        Assert.Equal(3, Directory.GetFiles(Path.GetDirectoryName(second)!, "*.spv").Length);
    }

    [Fact]
    public void Manifest_MatrixLayout_ReachesSlang()
    {
        string manifest = Fixture.CopyShaders(_temp.Combine("row"), withSpirv: false);
        string json = File.ReadAllText(manifest);
        Assert.Contains("\"column-major\"", json, StringComparison.Ordinal);
        File.WriteAllText(manifest, json.Replace("\"column-major\"", "\"row-major\"", StringComparison.Ordinal));
        string cli = _temp.Combine("cli");

        Assert.Equal(CookExitCode.Success, ShaderCommand.Run(["--manifest", manifest], _stdout, _stderr));
        Assert.Equal(CookExitCode.Success, CookTriangle(cli, "--matrix-layout", "row-major"));

        byte[] rowMajor = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(manifest)!, "triangle.vertMain.spv"));
        Assert.Equal(File.ReadAllBytes(Path.Combine(cli, "triangle.vertMain.spv")), rowMajor);
        Assert.NotEqual(File.ReadAllBytes(Fixture.Shader("triangle.vertMain.spv")), rowMajor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Manifest_SyntaxError_ReturnsCompileFailedAndWritesNothing(bool check)
    {
        string directory = _temp.Combine("bad");
        Directory.CreateDirectory(directory);
        File.Copy(Fixture.Path("syntax-error.slang"), Path.Combine(directory, "syntax-error.slang"));
        string manifest = Path.Combine(directory, "shaders.cook.json");
        File.WriteAllText(manifest, """{"version":1,"shaders":[{"source":"syntax-error.slang","entries":[{"name":"vertMain","stage":"vertex"}]}]}""");

        CookExitCode exit = ShaderCommand.Run(check ? ["--check", "--manifest", manifest] : ["--manifest", manifest], _stdout, _stderr);

        Assert.Equal(CookExitCode.CompileFailed, exit);
        Assert.Contains("error[E", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, Directory.GetFiles(directory).Length);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("a directory")]
    public void Manifest_MissingManifest_ReturnsInputNotFound(string kind)
    {
        string manifest = _temp.Combine("shaders.cook.json");
        if (kind == "a directory")
        {
            Directory.CreateDirectory(manifest);
        }

        var runtime = new CountingSlangRuntime();

        CookExitCode exit = ShaderCommand.Run(["--manifest", manifest], _stdout, _stderr, runtime);

        Assert.Equal(CookExitCode.InputNotFound, exit);
        Assert.StartsWith($"brutal-cook shader: can't read manifest '{manifest}': ", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, runtime.Created);
    }

    [Theory]
    [InlineData("../outside.slang")]
    [InlineData("sub/../../outside.slang")]
    [InlineData("absolute")]
    public void Manifest_SourceOutsideManifestDirectory_ReturnsUsageBeforeSlang(string source)
    {
        string outside = _temp.Combine("outside.slang");
        File.Copy(Fixture.Shader("triangle.slang"), outside);
        string manifest = _temp.Combine(Path.Combine("m", "shaders.cook.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        if (source == "absolute")
        {
            source = outside.Replace('\\', '/');
        }

        File.WriteAllText(
            manifest,
            $$"""{"version":1,"shaders":[{"source":"{{source}}","entries":[{"name":"vertMain","stage":"vertex"}]}]}""");
        var runtime = new CountingSlangRuntime();

        CookExitCode exit = ShaderCommand.Run(["--manifest", manifest], _stdout, _stderr, runtime);

        Assert.Equal(CookExitCode.Usage, exit);
        Assert.StartsWith($"brutal-cook shader: {manifest}: $.shaders[0].source: '{source}' ", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, runtime.Created);
        Assert.Equal([outside], Directory.GetFiles(_temp.Path));
        Assert.Equal([manifest], Directory.GetFiles(Path.GetDirectoryName(manifest)!));
        Assert.Empty(_stdout.ToString());
    }

    [Fact]
    public void Manifest_MissingSource_ReturnsInputNotFoundBeforeCompiling()
    {
        string manifest = Fixture.CopyShaders(_temp.Combine("shaders"), withSpirv: false);
        string compute = Path.Combine(Path.GetDirectoryName(manifest)!, "compute.slang");
        File.Delete(compute);
        var runtime = new CountingSlangRuntime();

        CookExitCode exit = ShaderCommand.Run(["--manifest", manifest], _stdout, _stderr, runtime);

        Assert.Equal(CookExitCode.InputNotFound, exit);
        Assert.Equal($"brutal-cook shader: source file '{compute}' not found.{Environment.NewLine}", _stderr.ToString());
        Assert.Equal(0, runtime.Created);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(manifest)!, "*.spv"));
    }

    [Fact]
    public void Manifest_LaterManifestInvalid_ReturnsUsageAndWritesNothing()
    {
        string first = Fixture.CopyShaders(_temp.Combine("first"), withSpirv: false);
        string second = _temp.Combine("second.cook.json");
        File.WriteAllText(second, """{"version":2}""");
        var runtime = new CountingSlangRuntime();

        CookExitCode exit = ShaderCommand.Run(["--manifest", first, "--manifest", second], _stdout, _stderr, runtime);

        Assert.Equal(CookExitCode.Usage, exit);
        Assert.Equal(64, (int)exit);
        Assert.Equal($"brutal-cook shader: {second}: $.version: expected 1, the only manifest version.{Environment.NewLine}", _stderr.ToString());
        Assert.Equal(0, runtime.Created);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(first)!, "*.spv"));
    }

    [Fact]
    public void Manifest_UnwritableOutput_ReturnsOutputFailed()
    {
        string manifest = Fixture.CopyShaders(_temp.Combine("shaders"), withSpirv: false);
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(manifest)!, "triangle.vertMain.spv"));

        CookExitCode exit = ShaderCommand.Run(["--manifest", manifest], _stdout, _stderr);

        Assert.Equal(CookExitCode.OutputFailed, exit);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(manifest)!, "compute.csMain.spv")));
    }

    [Fact]
    public void Manifest_UsageError_PrintsErrorAndUsageToStandardError()
    {
        var runtime = new CountingSlangRuntime();

        CookExitCode exit = ShaderCommand.Run(["--check"], _stdout, _stderr, runtime);

        Assert.Equal(CookExitCode.Usage, exit);
        Assert.StartsWith("brutal-cook shader: --check needs at least one --manifest.", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("brutal-cook shader --manifest <shaders.cook.json>", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(_stdout.ToString());
        Assert.Equal(0, runtime.Created);
    }

    [Fact]
    public void Check_MatchingFiles_Succeeds()
    {
        string manifest = Fixture.CopyShaders(_temp.Combine("shaders"), withSpirv: true);
        string directory = Path.GetDirectoryName(manifest)!;

        CookExitCode exit = ShaderCommand.Run(["--check", "--manifest", manifest], _stdout, _stderr);

        Assert.Equal(CookExitCode.Success, exit);
        Assert.Equal(
            Lines(
                $"matches: {Path.Combine(directory, "triangle.vertMain.spv")}",
                $"matches: {Path.Combine(directory, "triangle.fragMain.spv")}",
                $"matches: {Path.Combine(directory, "compute.csMain.spv")}"),
            _stdout.ToString());
        Assert.Empty(_stderr.ToString());
    }

    [Fact]
    public void Check_ChangedByte_ReturnsCheckFailedAndNamesFile()
    {
        string manifest = Fixture.CopyShaders(_temp.Combine("shaders"), withSpirv: true);
        string changed = Path.Combine(Path.GetDirectoryName(manifest)!, "triangle.fragMain.spv");
        byte[] bytes = File.ReadAllBytes(changed);
        bytes[100] ^= 0x01;
        File.WriteAllBytes(changed, bytes);

        CookExitCode exit = ShaderCommand.Run(["--manifest", manifest, "--check"], _stdout, _stderr);

        Assert.Equal(CookExitCode.CheckFailed, exit);
        Assert.Equal(6, (int)exit);
        Assert.Equal(
            $"differs: {changed} (expected {bytes.Length} bytes, cooked {bytes.Length} bytes, first difference at byte 100){Environment.NewLine}",
            _stderr.ToString());
        Assert.DoesNotContain(changed, _stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("matches: ", _stdout.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("one byte longer")]
    public void Check_DifferentLength_ReturnsCheckFailedWithBothSizes(string change)
    {
        string manifest = Fixture.CopyShaders(_temp.Combine("shaders"), withSpirv: true);
        string path = Path.Combine(Path.GetDirectoryName(manifest)!, "compute.csMain.spv");
        byte[] cooked = File.ReadAllBytes(path);
        byte[] committed = change == "truncated" ? cooked[..^4] : [.. cooked, 0];
        File.WriteAllBytes(path, committed);

        CookExitCode exit = ShaderCommand.Run(["--check", "--manifest", manifest], _stdout, _stderr);

        Assert.Equal(CookExitCode.CheckFailed, exit);
        Assert.Equal(
            $"differs: {path} (expected {committed.Length} bytes, cooked {cooked.Length} bytes, first difference at byte {Math.Min(committed.Length, cooked.Length)}){Environment.NewLine}",
            _stderr.ToString());
    }

    [Fact]
    public void Check_MissingFile_ReturnsCheckFailed()
    {
        string manifest = Fixture.CopyShaders(_temp.Combine("shaders"), withSpirv: true);
        string missing = Path.Combine(Path.GetDirectoryName(manifest)!, "compute.csMain.spv");
        File.Delete(missing);

        CookExitCode exit = ShaderCommand.Run(["--check", "--manifest", manifest], _stdout, _stderr);

        Assert.Equal(CookExitCode.CheckFailed, exit);
        Assert.Equal(
            $"differs: {missing} (missing; cooked {File.ReadAllBytes(Fixture.Shader("compute.csMain.spv")).Length} bytes){Environment.NewLine}",
            _stderr.ToString());
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public void Check_UnreadableFile_ReturnsCheckFailed()
    {
        string manifest = Fixture.CopyShaders(_temp.Combine("shaders"), withSpirv: true);
        string locked = Path.Combine(Path.GetDirectoryName(manifest)!, "triangle.vertMain.spv");
        CookExitCode exit;

        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            exit = ShaderCommand.Run(["--check", "--manifest", manifest], _stdout, _stderr);
        }

        Assert.Equal(CookExitCode.CheckFailed, exit);
        Assert.StartsWith($"differs: {locked} (can't be read: ", _stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Check_NeverWrites()
    {
        string manifest = Fixture.CopyShaders(_temp.Combine("shaders"), withSpirv: true);
        string directory = Path.GetDirectoryName(manifest)!;
        string changed = Path.Combine(directory, "triangle.vertMain.spv");
        string missing = Path.Combine(directory, "compute.csMain.spv");
        byte[] bytes = File.ReadAllBytes(changed);
        bytes[^1] ^= 0x80;
        File.WriteAllBytes(changed, bytes);
        File.Delete(missing);
        var past = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        foreach (string file in Directory.GetFiles(directory))
        {
            File.SetLastWriteTimeUtc(file, past);
        }

        Directory.SetLastWriteTimeUtc(directory, past);
        string before = Snapshot(directory);

        CookExitCode exit = ShaderCommand.Run(["--check", "--manifest", manifest], _stdout, _stderr);

        Assert.Equal(CookExitCode.CheckFailed, exit);
        Assert.Contains($"differs: {changed} (expected ", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains($"differs: {missing} (missing; ", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(directory));
        Assert.Equal(past, Directory.GetLastWriteTimeUtc(directory));
    }

    // Every entry's name, write time and contents.
    private static string Snapshot(string directory) =>
        string.Join(
            Environment.NewLine,
            Directory.GetFileSystemEntries(directory).Order(StringComparer.Ordinal).Select(entry =>
                $"{entry} {File.GetLastWriteTimeUtc(entry):O} {(File.Exists(entry) ? Convert.ToBase64String(File.ReadAllBytes(entry)) : "directory")}"));

    private static string Lines(params string[] lines) => string.Concat(lines.Select(line => line + Environment.NewLine));

    private CookExitCode CookTriangle(string output, params string[] extra) =>
        ShaderCommand.Run(
            [Fixture.Shader("triangle.slang"), "--entry", "vertMain:vertex", "--entry", "fragMain:fragment", "--output-dir", output, .. extra],
            _stdout,
            _stderr);
}
