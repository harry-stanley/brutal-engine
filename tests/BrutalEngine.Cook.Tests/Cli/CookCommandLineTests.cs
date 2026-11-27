using System;
using System.Linq;
using BrutalEngine.Cook.Cli;
using BrutalEngine.Cook.Shaders;
using Xunit;

namespace BrutalEngine.Cook.Tests.Cli;

public sealed class CookCommandLineTests
{
    // Each row: the arguments after `shader`, then either the parsed options rendered as
    // "source|name:Stage,...|output-dir|Layout", or "error: <text the error must contain>".
    public static TheoryData<string[], string> Cases => new()
    {
        // Valid forms.
        { ["a.slang", "--entry", "vs:vertex", "--output-dir", "out"], "a.slang|vs:Vertex|out|ColumnMajor" },
        {
            ["--output-dir", "o", "--entry", "fs:fragment", "--matrix-layout", "row-major", "src/b.slang", "--entry", "cs:compute"],
            "src/b.slang|fs:Fragment,cs:Compute|o|RowMajor"
        },
        { ["a.slang", "--entry", "vs:vertex", "--output-dir", "out", "--matrix-layout", "column-major"], "a.slang|vs:Vertex|out|ColumnMajor" },
        { ["a.slang", "--entry", "_v9:vertex", "--entry", "A_b:fragment", "--output-dir", "out"], "a.slang|_v9:Vertex,A_b:Fragment|out|ColumnMajor" },
        { ["a.slang", "--entry", "main:vertex", "--entry", "other:vertex", "--output-dir", "-o"], "a.slang|main:Vertex,other:Vertex|-o|ColumnMajor" },

        // The source.
        { [], "error: no source file given." },
        { ["--entry", "vs:vertex", "--output-dir", "out"], "error: no source file given." },
        { ["a.slang", "b.slang", "--entry", "vs:vertex", "--output-dir", "out"], "error: the source file is given twice." },
        { ["", "--entry", "vs:vertex", "--output-dir", "out"], "error: the source file is empty." },

        // --entry.
        { ["a.slang", "--output-dir", "out"], "error: at least one --entry is required." },
        { ["a.slang", "--entry", "vs:vertex", "--entry", "vs:fragment", "--output-dir", "out"], "error: --entry 'vs' is given twice" },
        { ["a.slang", "--entry", "main:vertex", "--entry", "Main:fragment", "--output-dir", "out"], "error: --entry 'Main' is given twice" },
        { ["a.slang", "--entry", "vs", "--output-dir", "out"], "error: --entry 'vs': expected <name>:<vertex|fragment|compute>." },
        { ["a.slang", "--entry", "vs:", "--output-dir", "out"], "error: --entry 'vs:': expected <name>:" },
        { ["a.slang", "--entry", "vs:Vertex", "--output-dir", "out"], "error: --entry 'vs:Vertex': expected <name>:" },
        { ["a.slang", "--entry", "vs:vertex ", "--output-dir", "out"], "error: --entry 'vs:vertex ': expected <name>:" },
        { ["a.slang", "--entry", "vs:pixel", "--output-dir", "out"], "error: --entry 'vs:pixel': expected <name>:" },
        { ["a.slang", "--entry", "vs:1", "--output-dir", "out"], "error: --entry 'vs:1': expected <name>:" },
        { ["a.slang", "--entry", "vs:vertex:fragment", "--output-dir", "out"], "error: --entry 'vs:vertex:fragment': expected <name>:" },
        { ["a.slang", "--entry", ":vertex", "--output-dir", "out"], "error: --entry ':vertex': the entry name must be an ASCII identifier" },
        { ["a.slang", "--entry", "9vs:vertex", "--output-dir", "out"], "error: --entry '9vs:vertex': the entry name must be an ASCII identifier" },
        { ["a.slang", "--entry", " vs:vertex", "--output-dir", "out"], "error: --entry ' vs:vertex': the entry name must be an ASCII identifier" },
        { ["a.slang", "--entry", "../vs:vertex", "--output-dir", "out"], "error: --entry '../vs:vertex': the entry name must be an ASCII identifier" },
        { ["a.slang", "--entry", "v\u00e9:vertex", "--output-dir", "out"], "error: --entry 'v\u00e9:vertex': the entry name must be an ASCII identifier" },
        { ["a.slang", "--entry", "vs\n:vertex", "--output-dir", "out"], "error: --entry 'vs\n:vertex': the entry name must be an ASCII identifier" },
        { ["a.slang", "--output-dir", "out", "--entry"], "error: --entry needs a value." },

        // --output-dir.
        { ["a.slang", "--entry", "vs:vertex"], "error: --output-dir is required." },
        { ["a.slang", "--entry", "vs:vertex", "--output-dir", ""], "error: --output-dir is empty." },
        { ["a.slang", "--entry", "vs:vertex", "--output-dir", "a", "--output-dir", "b"], "error: --output-dir is given twice." },
        { ["a.slang", "--entry", "vs:vertex", "--output-dir"], "error: --output-dir needs a value." },

        // --matrix-layout.
        { ["a.slang", "--entry", "vs:vertex", "--output-dir", "out", "--matrix-layout", "ColumnMajor"], "error: unknown matrix layout 'ColumnMajor'" },
        { ["a.slang", "--entry", "vs:vertex", "--output-dir", "out", "--matrix-layout", "row-major "], "error: unknown matrix layout 'row-major '" },
        { ["a.slang", "--entry", "vs:vertex", "--output-dir", "out", "--matrix-layout", ""], "error: --matrix-layout is empty." },
        {
            ["a.slang", "--entry", "vs:vertex", "--output-dir", "out", "--matrix-layout", "row-major", "--matrix-layout", "row-major"],
            "error: --matrix-layout is given twice."
        },
        { ["a.slang", "--entry", "vs:vertex", "--output-dir", "out", "--matrix-layout"], "error: --matrix-layout needs a value." },

        // Unknown options.
        { ["a.slang", "--entry", "vs:vertex", "--output-dir", "out", "--verbose"], "error: unknown option '--verbose'." },
        { ["a.slang", "-e", "vs:vertex", "--output-dir", "out"], "error: unknown option '-e'." },
        { ["a.slang", "--Entry", "vs:vertex", "--output-dir", "out"], "error: unknown option '--Entry'." },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Parse_Cases(string[] args, string expected)
    {
        bool parsed = CookCommandLine.TryParseShader(args, out ShaderCookOptions? options, out string? error);

        if (expected.StartsWith("error: ", StringComparison.Ordinal))
        {
            Assert.False(parsed);
            Assert.Null(options);
            Assert.NotNull(error);
            Assert.Contains(expected["error: ".Length..], error, StringComparison.Ordinal);
        }
        else
        {
            Assert.True(parsed, error);
            Assert.Null(error);
            Assert.NotNull(options);
            string entries = string.Join(",", options.Entries.Select(e => $"{e.Name}:{e.Stage}"));
            Assert.Equal(expected, $"{options.Source}|{entries}|{options.OutputDirectory}|{options.MatrixLayout}");
        }
    }
}
