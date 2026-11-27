using System;
using System.Buffers.Binary;
using System.IO;
using Brutal.SlangApi;
using BrutalEngine.Cook.Shaders;
using Xunit;

namespace BrutalEngine.Cook.Tests.Shaders;

/// <summary>
/// Serialises the tests that load real Slang: they create global sessions, and concurrent use of the native library
/// from several sessions at once isn't something these tests mean to exercise.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SlangSerial
{
    public const string Name = "Slang";
}

/// <summary>A fresh directory under the temp path, deleted with everything in it on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "brutal-cook-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(string relative) => System.IO.Path.Combine(Path, relative);

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

internal static class Fixture
{
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}

internal static class SpirV
{
    public const uint Magic = 0x07230203;

    private const ushort OpEntryPoint = 15;

    /// <summary>The first word of the module, read little-endian as Slang writes it on x64.</summary>
    public static uint FirstWord(byte[] module) => BinaryPrimitives.ReadUInt32LittleEndian(module);

    /// <summary>The execution model of the module's only OpEntryPoint: 0 Vertex, 4 Fragment, 5 GLCompute (SPIR-V spec 3.3).</summary>
    public static uint EntryPointExecutionModel(byte[] module)
    {
        uint? model = null;
        for (int offset = 5 * sizeof(uint); offset < module.Length;)
        {
            uint word = BinaryPrimitives.ReadUInt32LittleEndian(module.AsSpan(offset));
            int wordCount = (int)(word >> 16);
            Assert.True(wordCount > 0, $"malformed SPIR-V instruction at byte {offset}");
            if ((ushort)word == OpEntryPoint)
            {
                Assert.Null(model);
                model = BinaryPrimitives.ReadUInt32LittleEndian(module.AsSpan(offset + sizeof(uint)));
            }

            offset += wordCount * sizeof(uint);
        }

        Assert.NotNull(model);
        return model.Value;
    }
}

/// <summary>
/// The counting seam around <see cref="SlangRuntime"/>: creates real sessions (unless told to fail) and records every
/// creation and release.
/// </summary>
internal sealed class CountingSlangRuntime : ISlangRuntime
{
    private readonly SlangRuntime _real = new();

    /// <summary>When set, <see cref="TryCreateGlobalSession"/> throws this instead of calling Slang.</summary>
    public Exception? CreateThrows { get; init; }

    /// <summary>When true, <see cref="TryCreateGlobalSession"/> returns E_FAIL and no session.</summary>
    public bool CreateFails { get; init; }

    public int Created { get; private set; }

    public int Released { get; private set; }

    public IGlobalSession? Session { get; private set; }

    public Result TryCreateGlobalSession(out IGlobalSession? session)
    {
        if (CreateThrows is not null)
        {
            throw CreateThrows;
        }

        if (CreateFails)
        {
            session = null;
            return new Result(unchecked((int)0x80004005));
        }

        Result result = _real.TryCreateGlobalSession(out session);
        Assert.True(result.IsSuccess, $"Slang.TryCreateGlobalSession returned {result}");
        Created++;
        Session = session;
        return result;
    }

    public void Release(IGlobalSession session)
    {
        Assert.Same(Session, session);
        Released++;
        _real.Release(session);
    }
}
