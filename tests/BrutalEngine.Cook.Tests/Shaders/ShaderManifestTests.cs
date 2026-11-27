using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BrutalEngine.Cook.Shaders;
using Xunit;

namespace BrutalEngine.Cook.Tests.Shaders;

public sealed class ShaderManifestTests
{
    // Never read from disk: TryRead takes the bytes. It only resolves sources against this path's directory.
    private static readonly string _manifestPath = Path.Combine(Path.GetTempPath(), "manifest-tests", "m", "shaders.cook.json");

    private const string Vs = "{'name':'vs','stage':'vertex'}";

    // Each row: the manifest with ' for ", then either the shaders rendered as "source|name:Stage,...|Layout;...", with
    // sources relative to the manifest's directory, or "error: <the JSON path and rule the error must contain>".
    public static TheoryData<string, string> Cases => new()
    {
        // Valid manifests.
        { M(S("a.slang")), "a.slang|vs:Vertex|ColumnMajor" },
        {
            "{'shaders':[{'matrixLayout':'row-major','entries':[{'stage':'fragment','name':'fs'},{'name':'cs','stage':'compute'}],"
                + "'source':'sub/b.slang'}," + S("a.slang", ",'matrixLayout':'column-major'") + "],'version':1}",
            "sub/b.slang|fs:Fragment,cs:Compute|RowMajor;a.slang|vs:Vertex|ColumnMajor"
        },
        { " \n{ 'version' : 1 ,\n 'shaders' : [ " + S("a/b/c.slang") + " ] }\n ", "a/b/c.slang|vs:Vertex|ColumnMajor" },
        { M(S("./sub/../c.slang")), "c.slang|vs:Vertex|ColumnMajor" },
        { M(S("..a.slang")), "..a.slang|vs:Vertex|ColumnMajor" },
        { M(S("a.slang") + "," + S("b.slang") + "," + S("sub/a.slang")), "a.slang|vs:Vertex|ColumnMajor;b.slang|vs:Vertex|ColumnMajor;sub/a.slang|vs:Vertex|ColumnMajor" },
        { "{'vers\\u0069on':1,'shaders':[" + S("a.slang") + "]}", "a.slang|vs:Vertex|ColumnMajor" },

        // Not JSON, or not one JSON value.
        { "", "error: invalid JSON" },
        { "   ", "error: invalid JSON" },
        { M(S("a.slang")).Replace("]}", "],}", StringComparison.Ordinal), "error: invalid JSON" },
        { "// comment\n" + M(S("a.slang")), "error: invalid JSON" },
        { M(S("a.slang")) + "{}", "error: invalid JSON" },
        { M(S("a.slang")) + "x", "error: invalid JSON" },
        { "{'version':1,'shaders':[", "error: invalid JSON" },

        // The root and its version.
        { "[]", "error: $: expected an object." },
        { "1", "error: $: expected an object." },
        { "{'shaders':[" + S("a.slang") + "]}", "error: $: 'version' is missing." },
        { "{'version':1}", "error: $: 'shaders' is missing." },
        { "{'version':'1','shaders':[" + S("a.slang") + "]}", "error: $.version: expected 1" },
        { "{'version':2,'shaders':[" + S("a.slang") + "]}", "error: $.version: expected 1" },
        { "{'version':0,'shaders':[" + S("a.slang") + "]}", "error: $.version: expected 1" },
        { "{'version':1.0,'shaders':[" + S("a.slang") + "]}", "error: $.version: expected 1" },
        { "{'version':4294967297,'shaders':[" + S("a.slang") + "]}", "error: $.version: expected 1" },
        { "{'version':true,'shaders':[" + S("a.slang") + "]}", "error: $.version: expected 1" },
        { "{'version':1,'shaders':[" + S("a.slang") + "],'extra':0}", "error: $: unknown property 'extra'." },
        { "{'Version':1,'shaders':[" + S("a.slang") + "]}", "error: $: unknown property 'Version'." },
        { "{'version':1,'version':1,'shaders':[" + S("a.slang") + "]}", "error: $: 'version' is given twice." },

        // The shader list and each shader.
        { "{'version':1,'shaders':{}}", "error: $.shaders: expected an array." },
        { "{'version':1,'shaders':[]}", "error: $.shaders: lists no shaders." },
        { M("'a.slang'"), "error: $.shaders[0]: expected an object." },
        { M("{'entries':[" + Vs + "]}"), "error: $.shaders[0]: 'source' is missing." },
        { M("{'source':'a.slang'}"), "error: $.shaders[0]: 'entries' is missing." },
        { M(S("a.slang") + ",{'source':'b.slang'}"), "error: $.shaders[1]: 'entries' is missing." },
        { M(S("a.slang", ",'output':'out'")), "error: $.shaders[0]: unknown property 'output'." },
        { M(S("a.slang", ",'source':'b.slang'")), "error: $.shaders[0]: 'source' is given twice." },

        // Sources.
        { M("{'source':1,'entries':[" + Vs + "]}"), "error: $.shaders[0].source: expected a string." },
        { M("{'source':null,'entries':[" + Vs + "]}"), "error: $.shaders[0].source: expected a string." },
        { M(S("")), "error: $.shaders[0].source: the source is empty." },
        { M(S("../a.slang")), "error: $.shaders[0].source: '../a.slang' isn't a file inside the manifest's directory." },
        { M(S("sub/../../a.slang")), "error: $.shaders[0].source: 'sub/../../a.slang' isn't a file inside" },
        { M(S("..")), "error: $.shaders[0].source: '..' isn't a file inside" },
        { M(S(".")), "error: $.shaders[0].source: '.' isn't a file inside" },
        { M(S("/abs/a.slang")), "error: $.shaders[0].source: '/abs/a.slang' is absolute; sources are relative to the manifest's directory." },
        { M(S("sub\\\\a.slang")), "error: $.shaders[0].source: 'sub\\a.slang' contains '\\'; separate directories with '/'" },
        { M(S("a\\u0000.slang")), "error: $.shaders[0].source: the source contains a NUL character." },
        { M(S("sub/")), "error: $.shaders[0].source: 'sub/' has no file name." },
        { M(S(".slang")), "error: $.shaders[0].source: '.slang' has no file name." },

        // Duplicate sources: the same outputs.
        { M(S("a.slang") + "," + S("a.slang")), "error: $.shaders[1].source: 'a.slang' writes the same files as $.shaders[0]" },
        { M(S("a.slang") + "," + S("A.slang")), "error: $.shaders[1].source: 'A.slang' writes the same files as $.shaders[0]" },
        { M(S("a.slang") + "," + S("./a.slang")), "error: $.shaders[1].source: './a.slang' writes the same files as $.shaders[0]" },
        { M(S("a.slang") + "," + S("a.hlsl")), "error: $.shaders[1].source: 'a.hlsl' writes the same files as $.shaders[0]" },
        { M(S("a.slang") + "," + S("b.slang") + "," + S("b.slang")), "error: $.shaders[2].source: 'b.slang' writes the same files as $.shaders[1]" },

        // Entries.
        { M("{'source':'a.slang','entries':{}}"), "error: $.shaders[0].entries: expected an array." },
        { M("{'source':'a.slang','entries':[]}"), "error: $.shaders[0].entries: lists no entries." },
        { M("{'source':'a.slang','entries':['vs']}"), "error: $.shaders[0].entries[0]: expected an object." },
        { M("{'source':'a.slang','entries':[{'stage':'vertex'}]}"), "error: $.shaders[0].entries[0]: 'name' is missing." },
        { M("{'source':'a.slang','entries':[{'name':'vs'}]}"), "error: $.shaders[0].entries[0]: 'stage' is missing." },
        { M("{'source':'a.slang','entries':[" + Vs + ",{'name':'fs'}]}"), "error: $.shaders[0].entries[1]: 'stage' is missing." },
        { M("{'source':'a.slang','entries':[{'name':'vs','stage':'vertex','kind':0}]}"), "error: $.shaders[0].entries[0]: unknown property 'kind'." },
        { M("{'source':'a.slang','entries':[{'name':'vs','name':'fs','stage':'vertex'}]}"), "error: $.shaders[0].entries[0]: 'name' is given twice." },
        { M("{'source':'a.slang','entries':[{'name':1,'stage':'vertex'}]}"), "error: $.shaders[0].entries[0].name: expected a string." },
        { M("{'source':'a.slang','entries':[{'name':'9vs','stage':'vertex'}]}"), "error: $.shaders[0].entries[0].name: '9vs' isn't an ASCII identifier" },
        { M("{'source':'a.slang','entries':[{'name':'v-s','stage':'vertex'}]}"), "error: $.shaders[0].entries[0].name: 'v-s' isn't an ASCII identifier" },
        { M("{'source':'a.slang','entries':[{'name':'../vs','stage':'vertex'}]}"), "error: $.shaders[0].entries[0].name: '../vs' isn't an ASCII identifier" },
        { M("{'source':'a.slang','entries':[{'name':'vs\\n','stage':'vertex'}]}"), "error: $.shaders[0].entries[0].name: 'vs\n' isn't an ASCII identifier" },
        { M("{'source':'a.slang','entries':[{'name':'','stage':'vertex'}]}"), "error: $.shaders[0].entries[0].name: '' isn't an ASCII identifier" },
        { M("{'source':'a.slang','entries':[{'name':'vs','stage':0}]}"), "error: $.shaders[0].entries[0].stage: expected a string." },
        { M("{'source':'a.slang','entries':[{'name':'vs','stage':'Vertex'}]}"), "error: $.shaders[0].entries[0].stage: unknown stage 'Vertex'; expected vertex, fragment or compute." },
        { M("{'source':'a.slang','entries':[{'name':'vs','stage':'pixel'}]}"), "error: $.shaders[0].entries[0].stage: unknown stage 'pixel'" },
        { M("{'source':'a.slang','entries':[{'name':'vs','stage':'vertex '}]}"), "error: $.shaders[0].entries[0].stage: unknown stage 'vertex '" },
        { M("{'source':'a.slang','entries':[" + Vs + ",{'name':'VS','stage':'fragment'}]}"), "error: $.shaders[0].entries[1].name: 'VS' is given twice" },

        // Matrix layouts.
        { M(S("a.slang", ",'matrixLayout':'Row-Major'")), "error: $.shaders[0].matrixLayout: unknown matrix layout 'Row-Major'; expected column-major or row-major." },
        { M(S("a.slang", ",'matrixLayout':''")), "error: $.shaders[0].matrixLayout: unknown matrix layout ''" },
        { M(S("a.slang", ",'matrixLayout':1")), "error: $.shaders[0].matrixLayout: expected a string." },
        { M(S("a.slang", ",'matrixLayout':null")), "error: $.shaders[0].matrixLayout: expected a string." },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Read_Cases(string manifest, string expected)
    {
        bool read = ShaderManifest.TryRead(_manifestPath, Encoding.UTF8.GetBytes(manifest.Replace('\'', '"')), out IReadOnlyList<ShaderCookOptions>? shaders, out string? error);

        if (expected.StartsWith("error: ", StringComparison.Ordinal))
        {
            Assert.False(read);
            Assert.Null(shaders);
            Assert.NotNull(error);
            Assert.StartsWith($"{_manifestPath}: ", error, StringComparison.Ordinal);
            Assert.Contains(expected["error: ".Length..], error, StringComparison.Ordinal);
        }
        else
        {
            Assert.True(read, error);
            Assert.Null(error);
            Assert.NotNull(shaders);
            Assert.Equal(expected, string.Join(";", shaders.Select(Render)));
        }
    }

    [Fact]
    public void Read_InvalidUtf8_IsUsageError()
    {
        byte[] json = Encoding.UTF8.GetBytes(M(S("a\u00e9.slang")).Replace('\'', '"'));
        int at = Array.IndexOf(json, (byte)0xC3);
        json[at] = 0xFF;

        bool read = ShaderManifest.TryRead(_manifestPath, json, out IReadOnlyList<ShaderCookOptions>? shaders, out string? error);

        Assert.False(read);
        Assert.Null(shaders);
        Assert.StartsWith($"{_manifestPath}: invalid JSON", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../outside.slang")]
    [InlineData("sub/../../outside.slang")]
    [InlineData("absolute")]
    public void Read_PathOutsideManifestDirectory_IsUsageError(string source)
    {
        // A real tree, so the rejected source exists on disk: the rule is about where it is, not whether it is there.
        using var temp = new TempDirectory();
        string outside = temp.Combine("outside.slang");
        File.WriteAllText(outside, "// outside the manifest's directory\n");
        string manifest = temp.Combine(Path.Combine("m", "shaders.cook.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        if (source == "absolute")
        {
            source = outside.Replace('\\', '/');
        }

        File.WriteAllText(manifest, M("{'source':'" + source + "','entries':[" + Vs + "]}").Replace('\'', '"'));

        bool read = ShaderManifest.TryRead(manifest, File.ReadAllBytes(manifest), out IReadOnlyList<ShaderCookOptions>? shaders, out string? error);

        Assert.False(read);
        Assert.Null(shaders);
        Assert.StartsWith($"{manifest}: $.shaders[0].source: '{source}' ", error, StringComparison.Ordinal);
        Assert.Contains(source == outside.Replace('\\', '/') ? "is absolute" : "isn't a file inside the manifest's directory", error, StringComparison.Ordinal);
        Assert.Equal([outside], Directory.GetFiles(temp.Path));
        Assert.Equal([manifest], Directory.GetFiles(Path.GetDirectoryName(manifest)!));
    }

    private static string M(string shaders) => "{'version':1,'shaders':[" + shaders + "]}";

    private static string S(string source, string extra = "") => "{'source':'" + source + "','entries':[" + Vs + "]" + extra + "}";

    private static string Render(ShaderCookOptions shader)
    {
        Assert.Equal(Path.GetDirectoryName(shader.Source), shader.OutputDirectory);
        string source = Path.GetRelativePath(Path.GetDirectoryName(_manifestPath)!, shader.Source).Replace('\\', '/');
        string entries = string.Join(",", shader.Entries.Select(e => $"{e.Name}:{e.Stage}"));
        return $"{source}|{entries}|{shader.MatrixLayout}";
    }
}
