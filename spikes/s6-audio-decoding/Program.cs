// Spike S6 (throwaway, never merged): SDL3_mixer from Ahwoo.Brutal.Sdl.Mixer as a decoder only.
//
// What it measures, with every native status checked:
//   1. Lifetime: what works before MIX_Init, whether MIX_Init or a headless mixer needs (or starts) SDL's audio
//      subsystem, and which decoders the native build lists.
//   2. One-shot decode from memory (SDL_IOFromConstMem + MIX_CreateAudioDecoder_IO + MIX_DecodeAudio) of a WAV,
//      Ogg Vorbis, FLAC and MP3 file to 32-bit float PCM on a worker thread: frames, SHA-256, error against the
//      WAV source, and managed bytes allocated per MIX_DecodeAudio call after warm-up.
//   3. Streaming: the Ogg file through a headless MIX_CreateMixer + track (MIX_SetTrackIOStream over a memory
//      stream) + MIX_Generate on a worker thread, with one loop from a loop-start frame; the output is compared
//      with the one-shot decode around the loop point, plus managed bytes per MIX_Generate call.
//   4. Failure paths: a garbage buffer and a truncated Ogg file.
// Exit code 0 only if every check passed. Output lines starting with RESULT are compared across OSes.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Brutal.SdlApi;
using Brutal.SdlApi.Mixer;

namespace S6Probe;

internal static unsafe class Program
{
    private const int Rate = 44100;
    private const int Channels = 2;
    private const int SourceFrames = 44100; // tone.wav: 1.0 s
    private const long LoopStartFrame = 22050; // 0.5 s into the file
    private const int StageFrames = 1024;

    private static int s_failures;

    private static int Main(string[] args)
    {
        string audioDir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "audio");
        Console.WriteLine($"os: {RuntimeInformation.OSDescription} ({RuntimeInformation.RuntimeIdentifier})");
        Console.WriteLine($"runtime: {RuntimeInformation.FrameworkDescription}");
        // PublishAot=true makes RuntimeFeature report "no dynamic code" under JIT too, so the build mode is read
        // from the output instead: a JIT build has the managed S6Probe.dll beside the executable, NativeAOT doesn't.
        bool jit = File.Exists(Path.Combine(AppContext.BaseDirectory, "S6Probe.dll"));
        Console.WriteLine($"build mode: {(jit ? "JIT" : "NativeAOT")} (process {Environment.ProcessPath})");
        Console.WriteLine($"SDL_AUDIO_DRIVER env: {Environment.GetEnvironmentVariable("SDL_AUDIO_DRIVER") ?? "(unset)"}");
        Console.WriteLine($"audio dir: {audioDir}");

        try
        {
            Run(audioDir);
        }
        catch (Exception ex)
        {
            Fail($"unhandled exception: {ex}");
        }

        Console.WriteLine(s_failures == 0 ? "S6 PASS" : $"S6 FAIL ({s_failures} failed checks)");
        return s_failures == 0 ? 0 : 1;
    }

    private static void Run(string audioDir)
    {
        Console.WriteLine($"SDL version: {Sdl.PInvoke.GetVersion()}, SDL_mixer version: {SdlMixer.PInvoke.Version()}");
        PrintLoadedSdlModules();

        // --- 1. Lifetime -------------------------------------------------------------------------------------
        Console.WriteLine($"audio subsystem before MIX_Init: {AudioInitState()}");
        byte[] wavBytes = File.ReadAllBytes(Path.Combine(audioDir, "tone.wav"));
        using (NativeBuffer probe = NativeBuffer.CopyOf(wavBytes))
        {
            nint io = Sdl.PInvoke.IOFromConstMem(probe.Pointer, (nuint)probe.Length);
            Check(io != 0, "SDL_IOFromConstMem (before MIX_Init)");
            MixAudioDecoder early = SdlMixer.PInvoke.CreateAudioDecoderIO(io, 1, 0);
            Console.WriteLine(
                $"MIX_CreateAudioDecoder_IO before MIX_Init: {(early.IsNull() ? "null, SDL_GetError: " + Sdl.PInvoke.GetError() : "a decoder")}"
            );
            Console.WriteLine($"RESULT before-init decoder={(early.IsNull() ? "refused" : "created")}");
            if (!early.IsNull())
            {
                SdlMixer.PInvoke.DestroyAudioDecoder(early);
            }
        }

        Check(SdlMixer.PInvoke.Init() != 0, "MIX_Init");
        Console.WriteLine($"audio subsystem after MIX_Init: {AudioInitState()}");
        Console.WriteLine($"RESULT mix-init-starts-audio={(Sdl.PInvoke.WasInit(SdlInitFlags.Audio) != 0 ? "yes" : "no")}");

        int decoderCount = SdlMixer.PInvoke.GetNumAudioDecoders();
        Check(decoderCount > 0, "MIX_GetNumAudioDecoders > 0");
        var names = new List<string>();
        for (int i = 0; i < decoderCount; i++)
        {
            string? name = SdlMixer.PInvoke.GetAudioDecoder(i);
            Check(name is not null, $"MIX_GetAudioDecoder({i})");
            names.Add(name ?? "?");
        }
        Console.WriteLine($"RESULT decoders count={decoderCount} names={string.Join(',', names)}");

        // --- 2. One-shot decodes on a worker thread ----------------------------------------------------------
        float[] reference = WavToFloat(wavBytes);
        var decoded = new Dictionary<string, float[]>();
        int mainThread = Environment.CurrentManagedThreadId;
        RunOnWorker("decode", () =>
        {
            Console.WriteLine($"decode thread: managed id {Environment.CurrentManagedThreadId} (main {mainThread})");
            foreach (string file in new[] { "tone.wav", "tone.ogg", "tone.flac", "tone.mp3" })
            {
                decoded[file] = DecodeFile(file, File.ReadAllBytes(Path.Combine(audioDir, file)), reference);
            }
        });

        // Lossless sources must match the WAV bit for bit (SDL converts s16 to f32 as x / 32768).
        CheckExact(decoded["tone.wav"], reference, "tone.wav decode == WAV source");
        CheckExact(decoded["tone.flac"], reference, "tone.flac decode == WAV source");
        Check(decoded["tone.ogg"].Length == reference.Length, "tone.ogg frame count == source frames");

        // --- 3. Streaming through a headless mixer -------------------------------------------------------------
        Console.WriteLine($"audio subsystem before MIX_CreateMixer: {AudioInitState()}");
        RunOnWorker("stream", () => StreamOgg(File.ReadAllBytes(Path.Combine(audioDir, "tone.ogg")), decoded["tone.ogg"], mainThread));
        Console.WriteLine($"audio subsystem after streaming: {AudioInitState()}");

        // --- 4. Failure paths --------------------------------------------------------------------------------
        FailurePaths(File.ReadAllBytes(Path.Combine(audioDir, "tone.ogg")));

        // --- 5. The same decode while SDL's audio subsystem is up (the backend's situation: a device is open) -----
        Check(Sdl.PInvoke.InitSubSystem(SdlInitFlags.Audio) != 0, $"SDL_InitSubSystem(AUDIO) {Sdl.PInvoke.GetError()}");
        Console.WriteLine($"audio subsystem after SDL_InitSubSystem: {AudioInitState()}");
        RunOnWorker("decode-with-audio", () =>
        {
            float[] again = DecodeFile("tone.ogg", File.ReadAllBytes(Path.Combine(audioDir, "tone.ogg")), reference);
            CheckExact(again, decoded["tone.ogg"], "tone.ogg decode with the audio subsystem up == decode without it");
        });
        Sdl.PInvoke.QuitSubSystem(SdlInitFlags.Audio);

        SdlMixer.Quit();
        Console.WriteLine($"audio subsystem after MIX_Quit: {AudioInitState()}");
        Sdl.PInvoke.Quit();
    }

    private static float[] DecodeFile(string file, byte[] bytes, float[] reference)
    {
        using NativeBuffer source = NativeBuffer.CopyOf(bytes);
        // Pass 1 warms up every path (JIT, first-call resolution); pass 2 is the measured one.
        float[] result = Array.Empty<float>();
        for (int pass = 1; pass <= 2; pass++)
        {
            nint io = Sdl.PInvoke.IOFromConstMem(source.Pointer, (nuint)source.Length);
            Check(io != 0, $"{file}: SDL_IOFromConstMem");
            // closeio = 1: the decoder owns the stream and closes it in MIX_DestroyAudioDecoder.
            MixAudioDecoder decoder = SdlMixer.PInvoke.CreateAudioDecoderIO(io, 1, 0);
            if (decoder.IsNull())
            {
                Fail($"{file}: MIX_CreateAudioDecoder_IO: {Sdl.PInvoke.GetError()}");
                Sdl.PInvoke.CloseIO(io);
                return result;
            }

            SdlAudioSpec native;
            Check(SdlMixer.PInvoke.GetAudioDecoderFormat(decoder, &native) != 0, $"{file}: MIX_GetAudioDecoderFormat");
            // Ask for float at the file's own rate and channel count, so nothing is resampled.
            var spec = new SdlAudioSpec { format = SdlAudioFormat.F32, channels = native.channels, freq = native.freq };
            int frameBytes = 4 * spec.channels;
            int stageBytes = StageFrames * frameBytes;
            using var stage = new NativeBuffer(stageBytes);
            using var output = new NativeBuffer(8 * Rate * frameBytes); // 8 s: far more than any test file

            long written = 0;
            int calls = 0;
            long allocatedBefore = 0;
            bool failed = false;
            while (true)
            {
                if (calls == 1)
                {
                    allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                }
                int got = SdlMixer.PInvoke.DecodeAudio(decoder, stage.Pointer, stageBytes, &spec);
                calls++;
                if (got < 0)
                {
                    failed = true;
                    break;
                }
                if (got == 0)
                {
                    break;
                }
                if (written + got > output.Length)
                {
                    failed = true;
                    break;
                }
                Buffer.MemoryCopy(stage.Pointer, (byte*)output.Pointer + written, output.Length - written, got);
                written += got;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            string error = failed ? Sdl.PInvoke.GetError() ?? "" : "";
            SdlMixer.PInvoke.DestroyAudioDecoder(decoder);
            Check(!failed, $"{file}: MIX_DecodeAudio loop {error}");

            var span = new ReadOnlySpan<float>(output.Pointer, (int)(written / 4));
            if (pass == 2)
            {
                result = span.ToArray();
                long frames = written / frameBytes;
                string hash = Convert.ToHexStringLower(SHA256.HashData(new ReadOnlySpan<byte>(output.Pointer, (int)written)));
                Console.WriteLine(
                    $"RESULT decode file={file} native={native.format}/{native.channels}ch/{native.freq}Hz frames={frames} sha256={hash}"
                );
                Console.WriteLine(
                    $"RESULT alloc decode file={file} calls-after-warmup={calls - 1} bytes={allocated} per-call={(calls > 1 ? allocated / (double)(calls - 1) : 0).ToString("0.###", CultureInfo.InvariantCulture)}"
                );
                Console.WriteLine($"  {file} vs the WAV source at zero lag: {Compare(span, reference)}");
                Check(allocated == 0, $"{file}: zero managed bytes over {calls - 1} MIX_DecodeAudio calls");
                Check(native.channels == Channels && native.freq == Rate, $"{file}: native format is 2 ch / 44100 Hz");
            }
        }
        return result;
    }

    private static void StreamOgg(byte[] oggBytes, float[] oggDecoded, int mainThread)
    {
        Console.WriteLine($"stream thread: managed id {Environment.CurrentManagedThreadId} (main {mainThread})");
        var spec = new SdlAudioSpec { format = SdlAudioFormat.F32, channels = Channels, freq = Rate };
        MixMixer mixer = SdlMixer.PInvoke.CreateMixer(&spec);
        if (mixer.IsNull())
        {
            Fail($"MIX_CreateMixer (headless): {Sdl.PInvoke.GetError()}");
            Console.WriteLine("RESULT headless-mixer=refused");
            return;
        }
        Console.WriteLine($"RESULT headless-mixer=created audio-subsystem-now={AudioInitState()}");

        using NativeBuffer source = NativeBuffer.CopyOf(oggBytes);
        MixTrack track = SdlMixer.PInvoke.CreateTrack(mixer);
        Check(!track.IsNull(), "MIX_CreateTrack");
        nint io = Sdl.PInvoke.IOFromConstMem(source.Pointer, (nuint)source.Length);
        Check(io != 0, "SDL_IOFromConstMem (stream)");
        // closeio = 1: the track owns the stream.
        Check(SdlMixer.PInvoke.SetTrackIOStream(track, io, 1) != 0, $"MIX_SetTrackIOStream {Sdl.PInvoke.GetError()}");

        uint props = Sdl.PInvoke.CreateProperties();
        Check(props != 0, "SDL_CreateProperties");
        Check(Sdl.PInvoke.SetNumberProperty(props, SdlMixer.Constants.PropPlayLoopsNumber, 1) != 0, "set loops = 1");
        Check(
            Sdl.PInvoke.SetNumberProperty(props, SdlMixer.Constants.PropPlayLoopStartFrameNumber, LoopStartFrame) != 0,
            "set loop_start_frame"
        );
        Check(SdlMixer.PInvoke.PlayTrack(track, props) != 0, $"MIX_PlayTrack {Sdl.PInvoke.GetError()}");
        Sdl.PInvoke.DestroyProperties(props);

        int frameBytes = 4 * Channels;
        int chunkBytes = StageFrames * frameBytes;
        using var chunk = new NativeBuffer(chunkBytes);
        using var output = new NativeBuffer(4 * Rate * frameBytes);
        long written = 0;
        int calls = 0;
        int callsWhilePlaying = 0;
        long allocatedBefore = 0;
        // Return values of every call after the first short one: the window records them without allocating.
        Span<int> after = stackalloc int[4];
        int afterCount = 0;
        int shortCall = -1;
        int shortBytes = 0;
        int errorCall = -1;
        while (written + chunkBytes <= output.Length && afterCount < after.Length)
        {
            if (calls == 1)
            {
                allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            }
            int got = SdlMixer.PInvoke.Generate(mixer, chunk.Pointer, chunkBytes);
            calls++;
            if (got < 0)
            {
                errorCall = calls;
                break;
            }
            if (shortCall >= 0)
            {
                after[afterCount++] = got;
            }
            else if (got < chunkBytes)
            {
                shortCall = calls;
                shortBytes = got;
            }
            Buffer.MemoryCopy(chunk.Pointer, (byte*)output.Pointer + written, output.Length - written, got);
            written += got;
            if (SdlMixer.PInvoke.TrackPlaying(track) != 0)
            {
                callsWhilePlaying = calls;
            }
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Check(errorCall < 0, $"MIX_Generate failed at call {errorCall}: {Sdl.PInvoke.GetError()}");
        Console.WriteLine(
            $"RESULT generate chunk-bytes={chunkBytes} first-short-call={shortCall} short-bytes={shortBytes} total-frames={written / frameBytes} calls-while-playing={callsWhilePlaying} later-returns={string.Join(',', after[..afterCount].ToArray())}"
        );

        var outSpan = new ReadOnlySpan<float>(output.Pointer, (int)(written / 4));
        int n = SourceFrames * Channels;
        int loop = (int)LoopStartFrame * Channels;
        int second = n - loop;
        Check(outSpan.Length >= n + second, "stream produced at least N + (N - loopStart) frames");
        Check(written / frameBytes == SourceFrames + second / Channels, "MIX_Generate output ends exactly after the looped pass");
        if (outSpan.Length >= n + second)
        {
            string first = Compare(outSpan[..n], oggDecoded);
            string looped = Compare(outSpan.Slice(n, second), oggDecoded.AsSpan(loop, second));
            Console.WriteLine($"  stream frames [0, {SourceFrames}) vs one-shot decode [0, {SourceFrames}): {first}");
            Console.WriteLine(
                $"  stream frames [{SourceFrames}, {SourceFrames + second / Channels}) vs one-shot decode [{LoopStartFrame}, {SourceFrames}): {looped}"
            );
            Console.WriteLine("  loop point, left channel (stream frame: value | one-shot frame: value):");
            for (int f = SourceFrames - 2; f < SourceFrames + 2; f++)
            {
                int src = f < SourceFrames ? f : (int)LoopStartFrame + (f - SourceFrames);
                Console.WriteLine(
                    FormattableString.Invariant($"    {f}: {outSpan[f * Channels]:R} | {src}: {oggDecoded[src * Channels]:R}")
                );
            }
            Console.WriteLine(
                $"RESULT stream loops=1 loop-start={LoopStartFrame} first-pass=[{first}] looped-pass=[{looped}]"
            );
            Check(first.StartsWith("exact", StringComparison.Ordinal), "stream first pass == one-shot decode");
            Check(looped.StartsWith("exact", StringComparison.Ordinal), "stream looped pass == one-shot decode from loop start");
        }
        Console.WriteLine(
            $"RESULT alloc generate calls-after-warmup={calls - 1} bytes={allocated} per-call={(calls > 1 ? allocated / (double)(calls - 1) : 0).ToString("0.###", CultureInfo.InvariantCulture)}"
        );
        Check(allocated == 0, $"zero managed bytes over {calls - 1} MIX_Generate calls");

        SdlMixer.PInvoke.DestroyTrack(track);
        SdlMixer.PInvoke.DestroyMixer(mixer);
    }

    private static void FailurePaths(byte[] oggBytes)
    {
        // Deterministic garbage (xorshift32), so every OS sees the same bytes.
        var garbage = new byte[4096];
        uint x = 2463534242;
        for (int i = 0; i < garbage.Length; i++)
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            garbage[i] = (byte)x;
        }
        using (NativeBuffer g = NativeBuffer.CopyOf(garbage))
        {
            nint io = Sdl.PInvoke.IOFromConstMem(g.Pointer, (nuint)g.Length);
            Check(io != 0, "SDL_IOFromConstMem (garbage)");
            MixAudioDecoder d = SdlMixer.PInvoke.CreateAudioDecoderIO(io, 1, 0);
            Console.WriteLine($"RESULT failure garbage decoder={(d.IsNull() ? "refused" : "created")} error=\"{(d.IsNull() ? Sdl.PInvoke.GetError() : "")}\"");
            Check(d.IsNull(), "garbage input is refused at MIX_CreateAudioDecoder_IO");
            if (!d.IsNull())
            {
                SdlMixer.PInvoke.DestroyAudioDecoder(d);
            }
        }

        using (NativeBuffer t = NativeBuffer.CopyOf(oggBytes.AsSpan(0, oggBytes.Length / 2)))
        {
            nint io = Sdl.PInvoke.IOFromConstMem(t.Pointer, (nuint)t.Length);
            Check(io != 0, "SDL_IOFromConstMem (truncated)");
            MixAudioDecoder d = SdlMixer.PInvoke.CreateAudioDecoderIO(io, 1, 0);
            if (d.IsNull())
            {
                Console.WriteLine($"RESULT failure truncated-ogg decoder=refused error=\"{Sdl.PInvoke.GetError()}\"");
                return;
            }
            var spec = new SdlAudioSpec { format = SdlAudioFormat.F32, channels = Channels, freq = Rate };
            using var stage = new NativeBuffer(StageFrames * 4 * Channels);
            long frames = 0;
            int last;
            while ((last = SdlMixer.PInvoke.DecodeAudio(d, stage.Pointer, stage.Length, &spec)) > 0)
            {
                frames += last / (4 * Channels);
            }
            string error = last < 0 ? Sdl.PInvoke.GetError() ?? "" : "";
            Console.WriteLine(
                $"RESULT failure truncated-ogg decoder=created frames={frames} end={(last < 0 ? "error" : "eof")} error=\"{error}\""
            );
            SdlMixer.PInvoke.DestroyAudioDecoder(d);
        }
    }

    private static string Compare(ReadOnlySpan<float> actual, ReadOnlySpan<float> expected)
    {
        int count = Math.Min(actual.Length, expected.Length);
        int mismatches = 0;
        double maxError = 0;
        double errorEnergy = 0;
        double signalEnergy = 0;
        for (int i = 0; i < count; i++)
        {
            double e = actual[i] - expected[i];
            if (BitConverter.SingleToInt32Bits(actual[i]) != BitConverter.SingleToInt32Bits(expected[i]))
            {
                mismatches++;
            }
            maxError = Math.Max(maxError, Math.Abs(e));
            errorEnergy += e * e;
            signalEnergy += (double)expected[i] * expected[i];
        }
        string lengths = actual.Length == expected.Length ? "" : $" (lengths {actual.Length} vs {expected.Length})";
        if (mismatches == 0 && actual.Length == expected.Length)
        {
            return $"exact over {count / Channels} frames";
        }
        double snr = errorEnergy == 0 ? double.PositiveInfinity : 10 * Math.Log10(signalEnergy / errorEnergy);
        return FormattableString.Invariant(
            $"differs: {mismatches} of {count} samples, max abs error {maxError:0.######}, SNR {snr:0.#} dB{lengths}"
        );
    }

    private static void CheckExact(float[] actual, float[] expected, string what) =>
        Check(Compare(actual, expected).StartsWith("exact", StringComparison.Ordinal), what);

    // tone.wav is the 44-byte-header PCM file written by audio/make-tone.py.
    private static float[] WavToFloat(byte[] wav)
    {
        ReadOnlySpan<short> pcm = MemoryMarshal.Cast<byte, short>(wav.AsSpan(44));
        var result = new float[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
        {
            result[i] = pcm[i] / 32768f;
        }
        return result;
    }

    private static string AudioInitState()
    {
        bool on = Sdl.PInvoke.WasInit(SdlInitFlags.Audio) != 0;
        return on ? $"initialised (driver {Sdl.PInvoke.GetCurrentAudioDriver()})" : "not initialised";
    }

    private static void PrintLoadedSdlModules()
    {
        foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
        {
            if (m.ModuleName.Contains("SDL3", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"loaded native: {m.FileName}");
            }
        }
    }

    private static void RunOnWorker(string name, Action work)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        { Name = name };
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60)))
        {
            Fail($"{name} thread did not finish in 60 s");
            return;
        }
        if (error is not null)
        {
            Fail($"{name} thread threw: {error}");
        }
    }

    private static void Check(bool ok, string what)
    {
        if (!ok)
        {
            Fail(what);
        }
    }

    private static void Fail(string what)
    {
        Interlocked.Increment(ref s_failures);
        Console.WriteLine($"CHECK FAILED: {what}");
    }

    // Unmanaged memory for decoder input and output, so measured loops touch no managed heap.
    private sealed class NativeBuffer : IDisposable
    {
        public NativeBuffer(int length)
        {
            Length = length;
            Pointer = NativeMemory.AllocZeroed((nuint)length);
        }

        public void* Pointer { get; private set; }

        public int Length { get; }

        public static NativeBuffer CopyOf(ReadOnlySpan<byte> bytes)
        {
            var b = new NativeBuffer(bytes.Length);
            bytes.CopyTo(new Span<byte>(b.Pointer, bytes.Length));
            return b;
        }

        public void Dispose()
        {
            NativeMemory.Free(Pointer);
            Pointer = null;
        }
    }
}
