using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using BrutalEngine.Cook.Cli;

namespace BrutalEngine.Cook.Shaders;

/// <summary>
/// Reads shader manifests (<c>shaders.cook.json</c>, docs/design/cook.md "Manifests"): the built-in shaders of one
/// consumer, each cooked next to its source. Parsed with <see cref="Utf8JsonReader"/> (no reflection) and strictly:
/// standard JSON only (no comments or trailing commas), every property known, required and given once.
/// </summary>
internal static class ShaderManifest
{
    /// <summary>The only manifest version this tool reads.</summary>
    public const int Version = 1;

    /// <summary>Parses one manifest.</summary>
    /// <param name="path">The manifest's path: sources resolve against its directory, and errors name it.</param>
    /// <param name="json">The manifest's bytes.</param>
    /// <param name="shaders">
    /// On success, the listed shaders in manifest order: absolute sources, each with its own directory as the output
    /// directory; otherwise null.
    /// </param>
    /// <param name="error">On failure, the manifest path, the JSON path and the broken rule; otherwise null.</param>
    /// <returns>
    /// False for invalid JSON; a version other than the number <see cref="Version"/>; an unknown, repeated or missing
    /// property; an empty shader or entry list; a source that is empty, absolute, uses '\', NUL or ':', has a segment
    /// ending in '.' or ' ' (other than "." and ".."), names no file, isn't inside the manifest's directory, or writes the same files as an earlier one (same directory and name, ignoring
    /// case and extension); an entry name that isn't an ASCII identifier or repeats another ignoring case; or a stage or
    /// matrix layout token other than the command line's.
    /// </returns>
    public static bool TryRead(
        string path,
        ReadOnlySpan<byte> json,
        [NotNullWhen(true)] out IReadOnlyList<ShaderCookOptions>? shaders,
        [NotNullWhen(false)] out string? error)
    {
        shaders = null;
        var reader = new Utf8JsonReader(json);
        try
        {
            shaders = ReadRoot(ref reader, Path.GetDirectoryName(Path.GetFullPath(path))!);
            error = null;
            return true;
        }
        catch (JsonException e)
        {
            error = $"{path}: invalid JSON: {e.Message}";
        }
        catch (InvalidOperationException e)
        {
            // GetString throws this for a string that isn't valid UTF-8.
            error = $"{path}: invalid JSON: {e.Message}";
        }
        catch (InvalidManifestException e)
        {
            error = $"{path}: {e.Message}";
        }

        return false;
    }

    private static List<ShaderCookOptions> ReadRoot(ref Utf8JsonReader reader, string directory)
    {
        reader.Read();
        Require(ref reader, JsonTokenType.StartObject, "$", "an object");
        bool versioned = false;
        List<ShaderCookOptions>? shaders = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (NextProperty(ref reader, "$", seen, out string name))
        {
            switch (name)
            {
                case "version":
                    if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int version) || version != Version)
                    {
                        throw new InvalidManifestException("$.version", $"expected {Version}, the only manifest version.");
                    }

                    versioned = true;
                    break;
                case "shaders":
                    shaders = ReadShaders(ref reader, directory);
                    break;
                default:
                    throw Unknown("$", name);
            }
        }

        // Past the root: the reader throws JsonException for anything after it but whitespace.
        reader.Read();
        return !versioned ? throw Missing("$", "version") : shaders ?? throw Missing("$", "shaders");
    }

    private static List<ShaderCookOptions> ReadShaders(ref Utf8JsonReader reader, string directory)
    {
        Require(ref reader, JsonTokenType.StartArray, "$.shaders", "an array");
        var shaders = new List<ShaderCookOptions>();

        // A source's directory and file name without extension, which name its outputs, to the shader's index.
        var outputs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        while (NextElement(ref reader))
        {
            string at = $"$.shaders[{shaders.Count}]";
            ShaderCookOptions shader = ReadShader(ref reader, at, directory, out string source);
            string stem = Path.Combine(shader.OutputDirectory, Path.GetFileNameWithoutExtension(shader.Source));
            if (!outputs.TryAdd(stem, shaders.Count))
            {
                throw new InvalidManifestException($"{at}.source", $"'{source}' writes the same files as $.shaders[{outputs[stem]}].");
            }

            shaders.Add(shader);
        }

        return shaders.Count > 0 ? shaders : throw new InvalidManifestException("$.shaders", "lists no shaders.");
    }

    private static ShaderCookOptions ReadShader(ref Utf8JsonReader reader, string at, string directory, out string source)
    {
        Require(ref reader, JsonTokenType.StartObject, at, "an object");
        string? given = null;
        string? fullPath = null;
        List<ShaderEntry>? entries = null;
        MatrixLayout layout = MatrixLayout.ColumnMajor;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (NextProperty(ref reader, at, seen, out string name))
        {
            switch (name)
            {
                case "source":
                    given = ReadString(ref reader, $"{at}.source");
                    fullPath = Resolve(given, $"{at}.source", directory);
                    break;
                case "entries":
                    entries = ReadEntries(ref reader, $"{at}.entries");
                    break;
                case "matrixLayout":
                    {
                        string token = ReadString(ref reader, $"{at}.matrixLayout");
                        layout = CookCommandLine.ParseMatrixLayout(token) ?? throw new InvalidManifestException(
                            $"{at}.matrixLayout", $"unknown matrix layout '{token}'; expected column-major or row-major.");
                        break;
                    }

                default:
                    throw Unknown(at, name);
            }
        }

        source = given ?? throw Missing(at, "source");
        return new ShaderCookOptions(fullPath!, entries ?? throw Missing(at, "entries"), Path.GetDirectoryName(fullPath)!, layout);
    }

    // The source's full path, if it names a file inside the manifest's directory in the portable form.
    private static string Resolve(string source, string at, string directory)
    {
        string? problem = source.Length == 0 ? "the source is empty."
            : source.Contains('\0', StringComparison.Ordinal) ? "the source contains a NUL character."
            : source.Contains('\\', StringComparison.Ordinal) ? $"'{source}' contains '\\'; separate directories with '/' on every OS."
            : Path.IsPathRooted(source) ? $"'{source}' is absolute; sources are relative to the manifest's directory."
            : source.Contains(':', StringComparison.Ordinal) ? $"'{source}' contains ':', which Windows reads as a drive or a stream."
            : HasTrimmedSegment(source) ? $"'{source}' has a segment ending in '.' or ' ', which Windows drops."
            : null;
        string fullPath = problem is null ? Path.GetFullPath(source, directory) : "";
        string relative = problem is null ? Path.GetRelativePath(directory, fullPath) : "";
        problem ??= relative is "." or ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                ? $"'{source}' isn't a file inside the manifest's directory."
            : Path.GetFileNameWithoutExtension(fullPath).Length == 0 ? $"'{source}' has no file name."
            : null;
        return problem is null ? fullPath : throw new InvalidManifestException(at, problem);
    }

    // Windows drops trailing dots and spaces from each path segment ("a.slang." opens "a.slang"), so such a source
    // would name a different file there than on Linux. "." and ".." are navigation, not names.
    private static bool HasTrimmedSegment(string source) =>
        Array.Exists(source.Split('/'), segment => segment is not ("." or "..") && segment.Length > 0 && segment[^1] is '.' or ' ');

    private static List<ShaderEntry> ReadEntries(ref Utf8JsonReader reader, string at)
    {
        Require(ref reader, JsonTokenType.StartArray, at, "an array");
        var entries = new List<ShaderEntry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (NextElement(ref reader))
        {
            string entryAt = $"{at}[{entries.Count}]";
            ShaderEntry entry = ReadEntry(ref reader, entryAt);
            if (!names.Add(entry.Name))
            {
                throw new InvalidManifestException(
                    $"{entryAt}.name", $"'{entry.Name}' is given twice (names must differ ignoring case: each one names an output file).");
            }

            entries.Add(entry);
        }

        return entries.Count > 0 ? entries : throw new InvalidManifestException(at, "lists no entries.");
    }

    private static ShaderEntry ReadEntry(ref Utf8JsonReader reader, string at)
    {
        Require(ref reader, JsonTokenType.StartObject, at, "an object");
        string? name = null;
        ShaderStage? stage = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (NextProperty(ref reader, at, seen, out string property))
        {
            switch (property)
            {
                case "name":
                    name = ReadString(ref reader, $"{at}.name");
                    if (!CookCommandLine.IsEntryName(name))
                    {
                        throw new InvalidManifestException(
                            $"{at}.name", $"'{name}' isn't an ASCII identifier (letters, digits and '_', not starting with a digit).");
                    }

                    break;
                case "stage":
                    {
                        string token = ReadString(ref reader, $"{at}.stage");
                        stage = CookCommandLine.ParseStage(token) ?? throw new InvalidManifestException(
                            $"{at}.stage", $"unknown stage '{token}'; expected vertex, fragment or compute.");
                        break;
                    }

                default:
                    throw Unknown(at, property);
            }
        }

        return new ShaderEntry(name ?? throw Missing(at, "name"), stage ?? throw Missing(at, "stage"));
    }

    // Reads the next property name of the current object and moves to its value; false at the object's end.
    private static bool NextProperty(ref Utf8JsonReader reader, string at, HashSet<string> seen, out string name)
    {
        reader.Read();
        if (reader.TokenType == JsonTokenType.EndObject)
        {
            name = "";
            return false;
        }

        name = reader.GetString()!;
        if (!seen.Add(name))
        {
            throw new InvalidManifestException(at, $"'{name}' is given twice.");
        }

        reader.Read();
        return true;
    }

    // Moves to the next element of the current array; false at the array's end.
    private static bool NextElement(ref Utf8JsonReader reader)
    {
        reader.Read();
        return reader.TokenType != JsonTokenType.EndArray;
    }

    private static void Require(ref Utf8JsonReader reader, JsonTokenType type, string at, string what)
    {
        if (reader.TokenType != type)
        {
            throw new InvalidManifestException(at, $"expected {what}.");
        }
    }

    private static string ReadString(ref Utf8JsonReader reader, string at)
    {
        Require(ref reader, JsonTokenType.String, at, "a string");
        return reader.GetString()!;
    }

    private static InvalidManifestException Unknown(string at, string name) => new(at, $"unknown property '{name}'.");

    private static InvalidManifestException Missing(string at, string name) => new(at, $"'{name}' is missing.");

    // One broken rule, carried from the reader to TryRead, which turns it into the error; it never leaves this class.
    private sealed class InvalidManifestException(string at, string message) : Exception($"{at}: {message}");
}
