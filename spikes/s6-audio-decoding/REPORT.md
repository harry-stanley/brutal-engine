# Spike S6: compressed audio decoding with SDL3_mixer (prototype report)

Throwaway code on `spike/s6-audio-decoding`, never merged. The decision is ADR-0017 on `main`.

## What ran

`S6Probe` (net11.0, `Ahwoo.Brutal.Sdl` 2026.10.3, `Ahwoo.Brutal.Sdl.Mixer` 2026.10.3, `Microsoft.Extensions.ObjectPool`
11.0.0; SDL 3.4.12, SDL_mixer 3.2.4) with `SDL_AUDIO_DRIVER=dummy`, built and run twice per platform: JIT
(`dotnet build` + `dotnet run --no-build`) and NativeAOT (`dotnet publish -r <rid>`, then the native executable).

| Where | JIT | NativeAOT | Evidence |
|-------|-----|-----------|----------|
| linux-x64, reference machine (Arch Linux / CachyOS, kernel 7.3.6, .NET SDK 11.0.100, runtime 11.0.0, clang 23.1.5) | PASS | PASS, 0 publish warnings | local run, 2026-11-27 |
| linux-x64, GitHub `ubuntu-24.04` (image 20261122.23.1) | PASS | PASS, 0 publish warnings | [run 45910777898](https://github.com/harry-stanley/brutal-engine/actions/runs/45910777898), job 140218323763 |
| win-x64, GitHub `windows-2025` (image win25-vs2026 20261116.315.1) | PASS | PASS, 0 publish warnings | [run 45910777898](https://github.com/harry-stanley/brutal-engine/actions/runs/45910777898), job 140218323454 |

Not run: macOS (ADR-0015), and real audio output (SDL's dummy driver; no device plays anything).

Every run printed the same 19 `RESULT` lines: the workflow diffs JIT against NativeAOT per OS, and the SHA-256 of the
lines is `1c011c80fc01b2c1872e6971ede2e00ee29fe4880cd30a63775983c504b8c9b5` on all three platforms in both modes.
Native libraries resolved from the build output in each mode (`runtimes/<rid>/native/` under JIT, beside the executable
under NativeAOT); the probe prints the loaded paths.

## Test audio

Generated, no third-party audio: `audio/make-tone.py` writes `tone.wav` (1.0 s, 44100 Hz, stereo s16; 440 Hz left,
660 Hz right, amplitude 0.5, 10 ms fades). `audio/encode.sh` makes `tone.ogg` (ffmpeg n9.1, libvorbis, q4),
`tone.flac` (flac 1.5.0, `--best`) and `tone.mp3` (LAME 4.0, 128 kbit/s). Re-running both scripts reproduced the same bytes.

## Results

1. **Lifetime.** `MIX_CreateAudioDecoder_IO` before `MIX_Init` returns null with "Mixer not initialized (call MIX_Init
   first)". `MIX_Init` does not start SDL's audio subsystem (`SDL_WasInit(AUDIO)` stays 0), and decoders and a headless
   `MIX_CreateMixer` work without it. A decode with the audio subsystem up (dummy driver) gives the same bytes.
2. **Decoders listed** (`MIX_GetNumAudioDecoders`/`MIX_GetAudioDecoder`), identical on every platform:
   `WAV,STBVORBIS,DRFLAC,VOC,AIFF,AU,DRMP3,SINEWAVE,RAW` (9). No Opus.
3. **One-shot decodes** to f32 at the file's own rate and channels, on a worker thread, from `SDL_IOFromConstMem`:

   | File | Native format | Frames | Against tone.wav | SHA-256 of the f32 output (all platforms) |
   |------|---------------|--------|------------------|------------------------------------------|
   | tone.wav | S16le/2/44100 | 44100 | bit-exact | `4ce6356879bfab39f5b50a5a4055de8dd6668fec4bd68a78227855dd4fefa30b` |
   | tone.flac | F32le/2/44100 | 44100 | bit-exact | `4ce6356879bfab39f5b50a5a4055de8dd6668fec4bd68a78227855dd4fefa30b` |
   | tone.ogg | F32le/2/44100 | 44100 | SNR 40.8 dB | `588a9136f6e84483417e6d96991339d23c11c98012ee7c5fbe79043d248a1b48` |
   | tone.mp3 | F32le/2/44100 | 44100 | SNR 26 dB | `65f8a290eb02f607dc9ff4c90d87cd082d029e7a555e0b9e986c49fcaa65b604` |

   dr_mp3 returned exactly the source's 44100 frames (encoder delay and padding removed). The lossy decodes are
   bit-identical across Linux and Windows builds of the native library.
4. **Streaming with one loop.** Headless mixer (f32, 2 ch, 44100 Hz) + track + `MIX_SetTrackIOStream` over a memory
   stream + `MIX_PlayTrack` with `loops = 1` and `loop_start_frame = 22050`, pulled with `MIX_Generate` in 1024-frame
   chunks on a worker thread. Output: frames [0, 44100) equal the one-shot Vorbis decode bit for bit, frames
   [44100, 66150) equal decode frames [22050, 44100) bit for bit. `MIX_Generate` returned the full 8192 bytes for 64
   calls, 4912 bytes (614 frames) on call 65, where the looped pass ended, then 0 on every later call.
5. **Allocation.** 0 managed bytes over 44 `MIX_DecodeAudio` calls per file after warm-up, and 0 over 68
   `MIX_Generate` + `MIX_TrackPlaying` calls, in every run (`GC.GetAllocatedBytesForCurrentThread`, background GC off).
   BRUTAL's per-call trace logging was off (no `LogSystem` initialised).
6. **Failure paths.** 4 KiB of garbage and the first half of `tone.ogg` are both refused at
   `MIX_CreateAudioDecoder_IO` with "Audio data is in unknown/unsupported/corrupt format": a truncated Ogg fails at open,
   not mid-stream.
