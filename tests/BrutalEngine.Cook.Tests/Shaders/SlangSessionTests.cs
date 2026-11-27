using System;
using System.IO;
using BrutalEngine.Cook.Shaders;
using Xunit;

namespace BrutalEngine.Cook.Tests.Shaders;

/// <summary>The global session's lifetime in <see cref="SlangShaderCompiler"/>, through the counting seam.</summary>
[Collection(SlangSerial.Name)]
public sealed class SlangSessionTests : IDisposable
{
    private readonly StringWriter _stderr = new();

    public void Dispose() => _stderr.Dispose();

    [Theory]
    [InlineData("success")]
    [InlineData("compile failure")]
    [InlineData("missing entry")]
    [InlineData("undefined stage")]
    [InlineData("undefined matrix layout")]
    public void Compile_AnyOutcome_ReleasesSession(string outcome)
    {
        var runtime = new CountingSlangRuntime();
        ShaderEntry vertex = new("vertMain", ShaderStage.Vertex);
        ShaderCookOptions options = outcome switch
        {
            "success" => Options("triangle.slang", vertex),
            "compile failure" => Options("syntax-error.slang", vertex),
            "missing entry" => Options("triangle.slang", vertex, new ShaderEntry("noSuchEntry", ShaderStage.Fragment)),
            "undefined stage" => Options("triangle.slang", vertex, new ShaderEntry("fragMain", (ShaderStage)(-1))),
            _ => Options("triangle.slang", vertex) with { MatrixLayout = (MatrixLayout)2 },
        };
        var compiler = new SlangShaderCompiler(runtime);
        byte[][] code = [];

        Exception? thrown = Record.Exception(() => compiler.Compile(options, _stderr, out code));

        Assert.Equal(1, runtime.Created);
        Assert.Equal(1, runtime.Released);
        switch (outcome)
        {
            case "success":
                Assert.Null(thrown);
                Assert.Single(code);
                break;
            case "compile failure" or "missing entry":
                Assert.Null(thrown);
                Assert.Empty(code);
                break;
            default:
                // A misuse that surfaces only once the session exists: the session is released on the way out.
                Assert.IsType<ArgumentOutOfRangeException>(thrown);
                break;
        }
    }

    [Fact]
    public void Compile_Success_ReturnsEachEntrysCodeInOrder()
    {
        var compiler = new SlangShaderCompiler(new CountingSlangRuntime());
        ShaderCookOptions options = Options(
            "triangle.slang", new ShaderEntry("fragMain", ShaderStage.Fragment), new ShaderEntry("vertMain", ShaderStage.Vertex));

        CookExitCode exit = compiler.Compile(options, _stderr, out byte[][] code);

        Assert.Equal(CookExitCode.Success, exit);
        Assert.Equal(2, code.Length);
        Assert.Equal(4u, SpirV.EntryPointExecutionModel(code[0]));
        Assert.Equal(0u, SpirV.EntryPointExecutionModel(code[1]));
    }

    [Fact]
    public void Compile_SessionCreationFails_ReturnsToolFailureWithoutRelease()
    {
        var runtime = new CountingSlangRuntime { CreateFails = true };
        var compiler = new SlangShaderCompiler(runtime);

        CookExitCode exit = compiler.Compile(Options("triangle.slang", new ShaderEntry("vertMain", ShaderStage.Vertex)), _stderr, out byte[][] code);

        Assert.Equal(CookExitCode.ToolFailure, exit);
        Assert.Empty(code);
        Assert.Equal(0, runtime.Released);
        Assert.Contains("couldn't create a Slang global session (0x80004005)", _stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("type initializer")]
    [InlineData("dll not found")]
    public void Compile_RuntimeFailsToLoad_ReturnsToolFailure(string failure)
    {
        var missing = new DllNotFoundException("Unable to load shared library 'slang'");
        var runtime = new CountingSlangRuntime
        {
            CreateThrows = failure == "dll not found" ? missing : new TypeInitializationException("PInvoke", missing),
        };
        var compiler = new SlangShaderCompiler(runtime);

        CookExitCode exit = compiler.Compile(Options("triangle.slang", new ShaderEntry("vertMain", ShaderStage.Vertex)), _stderr, out byte[][] code);

        Assert.Equal(CookExitCode.ToolFailure, exit);
        Assert.Empty(code);
        Assert.Equal(0, runtime.Released);
        Assert.Contains("the Slang runtime failed to load: Unable to load shared library 'slang'", _stderr.ToString(), StringComparison.Ordinal);
    }

    private static ShaderCookOptions Options(string fixture, params ShaderEntry[] entries) =>
        new(Fixture.Path(fixture), entries, "unused", MatrixLayout.ColumnMajor);
}
