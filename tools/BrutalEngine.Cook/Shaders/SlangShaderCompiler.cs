using System;
using System.Collections.Generic;
using System.IO;
using Brutal;
using Brutal.SlangApi;

namespace BrutalEngine.Cook.Shaders;

/// <summary>
/// Compiles one Slang source to SPIR-V, one module per entry point, through BRUTAL's compile-request API
/// (Brutal.Slang/Generated/Structs.gen.cs). One global session per call, released on every path.
/// </summary>
/// <param name="runtime">Creates and releases the global session.</param>
internal sealed class SlangShaderCompiler(ISlangRuntime runtime)
{
    // SPIR-V 1.6, the version Vulkan 1.3 requires drivers to accept (ADR-0002's target). Explicitly NUL-terminated.
    private static ReadOnlySpan<byte> ProfileName => "spirv_1_6\0"u8;

    /// <summary>Compiles every entry point of <paramref name="options"/>' source.</summary>
    /// <param name="options">The source, entry points and matrix layout. The source must exist.</param>
    /// <param name="stderr">Receives Slang's diagnostics unchanged (warnings too, on success), and tool errors.</param>
    /// <param name="code">On success, each entry's SPIR-V in <c>options.Entries</c> order (copies owned by the caller); otherwise empty.</param>
    /// <returns>
    /// <see cref="CookExitCode.Success"/>; <see cref="CookExitCode.CompileFailed"/> when Slang reports errors; or
    /// <see cref="CookExitCode.ToolFailure"/> when the Slang runtime doesn't load, or a session, profile, request or
    /// entry point's code is missing.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">An entry's stage or the matrix layout isn't a defined value; the session is still released.</exception>
    public CookExitCode Compile(ShaderCookOptions options, TextWriter stderr, out byte[][] code)
    {
        code = [];
        IGlobalSession? session = null;
        try
        {
            Result created = runtime.TryCreateGlobalSession(out session);
            if (created.IsFailure || session is null)
            {
                stderr.WriteLine($"brutal-cook shader: couldn't create a Slang global session ({created}).");
                return CookExitCode.ToolFailure;
            }

            return Compile(session, options, stderr, ref code);
        }
        catch (Exception e) when (e is TypeInitializationException or DllNotFoundException)
        {
            stderr.WriteLine($"brutal-cook shader: the Slang runtime failed to load: {e.GetBaseException().Message}");
            return CookExitCode.ToolFailure;
        }
        finally
        {
            if (session is not null)
            {
                runtime.Release(session);
            }
        }
    }

    private static unsafe CookExitCode Compile(IGlobalSession session, ShaderCookOptions options, TextWriter stderr, ref byte[][] code)
    {
        ProfileID profile;
        fixed (byte* name = ProfileName)
        {
            profile = session.FindProfile(name);
        }

        if (profile == ProfileID.Unknown)
        {
            stderr.WriteLine("brutal-cook shader: this Slang has no spirv_1_6 profile.");
            return CookExitCode.ToolFailure;
        }

        Result created = session.TryCreateCompileRequest(out ICompileRequest request);
        if (created.IsFailure)
        {
            stderr.WriteLine($"brutal-cook shader: couldn't create a Slang compile request ({created}).");
            return CookExitCode.ToolFailure;
        }

        using (request)
        {
            request.SetCodeGenTarget(CompileTarget.Spirv);
            request.SetTargetProfile(0, profile);
            request.SetMatrixLayoutMode(options.MatrixLayout switch
            {
                MatrixLayout.ColumnMajor => MatrixLayoutMode.ColumnMajor,
                MatrixLayout.RowMajor => MatrixLayoutMode.RowMajor,
                _ => throw new ArgumentOutOfRangeException(nameof(options), options.MatrixLayout, "Undefined matrix layout."),
            });
            int unit = request.AddTranslationUnit(SourceLanguage.Slang, NulTerminated.Utf8(Path.GetFileNameWithoutExtension(options.Source)));
            request.AddTranslationUnitSourceFile(unit, NulTerminated.Utf8(options.Source));
            var indices = new List<int>(options.Entries.Count);
            foreach (ShaderEntry entry in options.Entries)
            {
                indices.Add(request.AddEntryPoint(unit, NulTerminated.Utf8(entry.Name), ToSlang(entry.Stage)));
            }

            Result compiled = request.Compile();
            stderr.Write(request.GetDiagnosticOutput());
            if (compiled.IsFailure)
            {
                return CookExitCode.CompileFailed;
            }

            byte[][] outputs = new byte[indices.Count][];
            for (int i = 0; i < outputs.Length; i++)
            {
                void* bytes = request.GetEntryPointCode(indices[i], out ByteSize size);
                if (bytes == null)
                {
                    stderr.WriteLine($"brutal-cook shader: Slang produced no code for entry '{options.Entries[i].Name}'.");
                    return CookExitCode.ToolFailure;
                }

                // Sound: Slang owns `size` bytes at `bytes` until the request is disposed, after this copy.
                outputs[i] = new ReadOnlySpan<byte>(bytes, checked((int)(ulong)size)).ToArray();
            }

            code = outputs;
            return CookExitCode.Success;
        }
    }

    private static Stage ToSlang(ShaderStage stage) => stage switch
    {
        ShaderStage.Vertex => Stage.Vertex,
        ShaderStage.Fragment => Stage.Fragment,
        ShaderStage.Compute => Stage.Compute,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Undefined shader stage."),
    };
}
