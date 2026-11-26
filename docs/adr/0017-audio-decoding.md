# ADR-0017: Decode compressed audio with SDL3_mixer, used as a decoder only

- **Status:** Accepted
- **Date:** 2026-11-27
- **Deciders:** the maintainer

## Context

[ADR-0004](0004-audio.md) gives the engine its own mixer on SDL3 audio streams and leaves one gap: "Compressed streaming needs a decoder (spike S6, risk R13)." (`docs/adr/0004-audio.md:24`). This ADR fills it. The forces:

- **What a game needs.** One-shot sound effects: short, many at once, started with no latency. Music and long ambience: minutes long, too big to hold decoded, and looping from a point inside the file (an intro plays once, then the body loops).
- **The audio thread's rules** ([Architecture 3.11](../03-architecture.md#311-audio-brutalengineaudio-backend-audiosdl3)): our mixer runs on the audio thread, which never allocates, locks or blocks. So a decoder never runs on it; decoded PCM reaches it through memory that is already there.
- **Layers.** `BrutalEngine.Audio` is an L2 abstraction (our mixer and the device abstraction) and `BrutalEngine.Audio.Sdl3` is its L2 backend, the only kind of assembly allowed to expose BRUTAL binding types (`tests/BrutalEngine.Architecture.Tests/EngineLayers.cs:18` and `:23`). The cooker, `BrutalEngine.Cook`, is an L6 tool ([Architecture 3.1](../03-architecture.md#31-layering), [3.13](../03-architecture.md#313-assets-brutalengineassets-tool-brutalenginecook)).
- **What BRUTAL ships.** Spike S6's research found that `Ahwoo.Brutal.Sdl.Mixer` binds SDL_mixer 3.2.4, built with stb_vorbis, dr_flac, dr_mp3 and SDL_mixer's own WAV decoder, and without Opus ([S6 results](../spikes/README.md#s6-compressed-audio-decoding-prototype)). Its 2026.10.3 nuspec depends on `Ahwoo.Brutal.Sdl.Mixer.Native` 2026.10.3, `Ahwoo.Brutal.Sdl` 2026.10.3, `Ahwoo.Brutal.Core` 2026.10.4, `Ahwoo.Brutal.Interop` 2026.10.2 and `Ahwoo.Brutal.Logging` 2026.10.3. The native package has `SDL3_mixer.dll`, `libSDL3_mixer.so.0.2.4` and an osx-arm64 dylib. Both packages carry `LICENSES/BRUTAL-SDL-MIXER-THIRD-PARTY-NOTICES.txt` (with different headers): SDL_mixer is Zlib, stb_vorbis MIT OR Unlicense, dr_flac and dr_mp3 Unlicense OR MIT-0 (dr_mp3 also carries minimp3's public-domain dedication).
- **The binding** (`Brutal.Sdl.Mixer/Generated/SdlMixer.PInvoke.gen.cs`, namespace `Brutal.SdlApi.Mixer` at `:5`, BRUTAL checkout):
  - Library: `MIX_Init` `:21`, `MIX_Quit` `:31`, `MIX_GetNumAudioDecoders` `:41`, `MIX_GetAudioDecoder` `:53`.
  - One-shot decoding: `MIX_CreateAudioDecoder_IO` `:1018`, `MIX_GetAudioDecoderFormat` `:1048`, `MIX_DecodeAudio` `:1058`, `MIX_DestroyAudioDecoder` `:1028`. The decoder API has nothing else: `MIX_CreateAudioDecoder` (from a path) `:1005` and `MIX_GetAudioDecoderProperties` `:1038` complete it, and none of them seeks.
  - Mixing without a device: `MIX_CreateMixer` `:76` (no device; `MIX_CreateMixerDevice` `:66` is the one that opens one), `MIX_Generate` `:993`, `MIX_DestroyMixer` `:86`; tracks: `MIX_CreateTrack` `:283`, `MIX_SetTrackIOStream` `:343`, `MIX_PlayTrack` `:558`, `MIX_TrackPlaying` `:698`, `MIX_SetTrackPlaybackPosition` `:418`, `MIX_DestroyTrack` `:293`.
  - Play options are SDL properties: `PropPlayLoopsNumber` and `PropPlayLoopStartFrameNumber` (`Brutal.Sdl.Mixer/Generated/SdlMixer.Constants.gen.cs:36` and `:37`), set with `SDL_CreateProperties`, `SDL_SetNumberProperty` and `SDL_DestroyProperties` (`Brutal.Sdl/Generated/Sdl.PInvoke.gen.cs:2167`, `:2266`, `:2447`). Memory streams: `SDL_IOFromConstMem` `:2916`, `SDL_CloseIO` `:2946`.
  - Every generated wrapper calls `SdlMixer.TraceLog.Trace()` before the native call (`Brutal.Sdl.Mixer/Generated/SdlMixer.PInvoke.gen.cs:17`), on BRUTAL's `SdlMixer` log category (`Brutal.Sdl.Mixer/SdlMixer.cs:23`). The platform's log bridge keeps BRUTAL's trace level off (`docs/design/platform.md:540`).
- **The release freeze** ([ADR-0016](0016-trunk-based-milestones.md)). This decision is for M2 (0.3.0). No project that ships in 0.2.0 resolves `Ahwoo.Brutal.Sdl.Mixer`, so adding its version later changes no releasing lock file. This ADR adds no package version.
- **The prototype** (spike S6, on its own branch, run on 2026-11-27): see [What the prototype measured](#what-the-prototype-measured) and the [S6 results](../spikes/README.md#s6-compressed-audio-decoding-prototype).

If nothing is decided, `Audio.Sdl3` (M2) has no way to play music, and the cooker can't import any compressed sound.

## Options considered

1. **SDL3_mixer as a decoder only (`Ahwoo.Brutal.Sdl.Mixer`), in `Audio.Sdl3` and the cook-time audio importer.** Pros: BRUTAL already builds, pins and ships it for every RID we build, next to the SDL3 we use; WAV, Ogg Vorbis, FLAC and MP3 in one dependency; the prototype found it allocation-free per call, NativeAOT-clean, and bit-identical across Linux and Windows. Its licence notices are already packaged. Cons: one more native library per RID; SDL_mixer's version moves only with BRUTAL's (a decoder bug waits for a BRUTAL release); no Opus; looping streams go through SDL_mixer's track object model (a headless mixer per stream) because the decoder API can't seek; the bindings take and return managed strings for property names, decoder names and errors, so those calls may allocate (setup and error paths, not measured).
2. **A managed decoder: NVorbis (MIT) in `BrutalEngine.Audio`.** Pros: no native code, debuggable in-process, nothing to bundle. Cons: Vorbis only, so FLAC and MP3 need more third-party libraries; third-party C# code in an L2 abstraction, where the engine writes its own libraries because outside ones generally don't meet the allocation rules ([Tech stack](../02-tech-stack.md#engine-side-libraries-ours-no-third-party-code)); its allocation behaviour, NativeAOT and trimming compatibility, and output on each OS would all need their own prototype (none was run).
3. **Build the decoders ourselves:** compile stb_vorbis, dr_flac and dr_mp3 (and later libopus) per RID in our own natives workflow, with our own `LibraryImport` bindings. Pros: we choose versions and fixes, and could add Opus. Cons: we own a C build for Windows and Linux (plus the macOS restore path), its signing, notices, CI time and security updates, to rebuild what BRUTAL already ships.

**No Opus** follows from option 1: BRUTAL's build doesn't include it. Opus is better than Vorbis at low bitrates, but Ogg Vorbis is enough for game music, and nothing in 0.3 needs Opus.

## Decision

Decision (maintainer, 2026-11-27): option 1. Options 2 and 3 are rejected for the reasons above.

- **Package and place.** `Ahwoo.Brutal.Sdl.Mixer` 2026.10.3 (with its `.Native` package), referenced only by `BrutalEngine.Audio.Sdl3` and the cook-time audio importer (L6). Never by `BrutalEngine.Audio` or any L3+ module, which see only PCM and engine types.
- **A decoder only.** Nothing plays through SDL_mixer: no device mixer (`MIX_CreateMixerDevice`), and no SDL_mixer gain, groups, effects, 3D positioning or callbacks. Our mixer ([ADR-0004](0004-audio.md)) does all mixing. The headless mixer used for streaming (below) is a decoding device: one per stream, with one track at the default gain.
- **Formats.** WAV, Ogg Vorbis, FLAC and MP3 as import inputs. No Opus in 0.3.
- **Short sounds are cooked to PCM.** The importer decodes them at cook time, so the runtime decodes nothing for them. The sample format and the short/long threshold are for the audio design note (#281).
- **Music and long ambience ship as Ogg Vorbis and stream.** The pack keeps the Ogg Vorbis file's bytes and a decoder worker decodes them while playing. Nothing in this stack encodes Vorbis, so the importer never re-encodes: what it does with a long sound whose source isn't Ogg Vorbis (reject it, or cook it to PCM) is for #281.

### How streaming and decoding work

The API level `Audio.Sdl3` uses. Facts marked *measured* come from the prototype; everything else is from the bindings and SDL_mixer's documentation, not observed, and #281's implementation issues verify it.

- **Library lifetime.** `Audio.Sdl3` calls `MIX_Init` once before creating any decoder or mixer, and BRUTAL's `SdlMixer.Quit` (`Brutal.Sdl.Mixer/Manual/Libraries/SdlMixer.Init.cs:5`, which also frees BRUTAL's callback handles) once after destroying all of them. Neither needs SDL's audio subsystem (*measured*), so the cooker decodes on hosts with no audio device, and `Audio.Sdl3` starts the audio subsystem for its device on its own (`SDL_InitSubSystem`, `Brutal.Sdl/Generated/Sdl.PInvoke.gen.cs:11938`).
- **Streams.** Per playing stream, a decoder worker (a thread or job owned by `Audio.Sdl3`, never the audio thread) owns:
  1. a headless mixer, `MIX_CreateMixer` with an f32 spec at the stream's rate and channel count;
  2. one track (`MIX_CreateTrack`) fed by `MIX_SetTrackIOStream` with `closeio` true, over `SDL_IOFromConstMem` on the pack's bytes, which must outlive the track;
  3. `MIX_PlayTrack` with a properties set holding the loop count and the loop-start frame, destroyed after the call;
  4. then `MIX_Generate` into a ring buffer that the audio thread reads without locks. `MIX_Generate` returns the full buffer while the track plays, a short count when it ends, then 0 (*measured*): the worker treats a short count as the end of the stream. Looping forever is `loops = -1` in SDL_mixer's documentation (not run).
- **One-shot decodes** (the cooker, and dev-mode raw assets under [ADR-0005](0005-offline-asset-cooking.md)): `SDL_IOFromConstMem`, `MIX_CreateAudioDecoder_IO` with `closeio` true, `MIX_GetAudioDecoderFormat`, then `MIX_DecodeAudio` with the wanted spec until it returns 0. A negative return is an error.
- **Seeking.** The decoder API can't seek, so seeking and looping use the track path: the play properties for loops (*measured*), `MIX_SetTrackPlaybackPosition` for a seek (not run).
- **Threads.** Decoders and headless mixers worked on threads other than the main thread, with SDL not initialised at all (*measured*). Each decoder and headless mixer is used by one thread at a time; concurrent use of one object wasn't tested, and isn't allowed.
- **Errors.** Creating a decoder from unknown, corrupt or truncated data returns null, with the reason in `SDL_GetError` (*measured*: garbage and a half-length Ogg file are both refused at creation). `Audio.Sdl3` maps every null or negative status to a `Result` failure ([Errors](../design/errors.md)).

### What the prototype measured

Spike S6 ran a throwaway probe on `spike/s6-audio-decoding` (generated test tones, details in the [S6 results](../spikes/README.md#s6-compressed-audio-decoding-prototype)) with `SDL_AUDIO_DRIVER=dummy`, JIT and NativeAOT each: locally on linux-x64 (the reference machine, Arch Linux), and on GitHub's `ubuntu-24.04` (linux-x64) and `windows-2025` (win-x64) runners in workflow [run 45910777898](https://github.com/harry-stanley/brutal-engine/actions/runs/45910777898) (jobs 140218323763 and 140218323454). All six runs passed and printed identical results.

- **Lifetime.** `MIX_CreateAudioDecoder_IO` before `MIX_Init` fails ("Mixer not initialized (call MIX_Init first)"). `MIX_Init` doesn't start SDL's audio subsystem, and decoders and a headless mixer work without it. The same decode with the audio subsystem started (dummy driver) gives the same bytes.
- **Decoders listed:** `WAV, STBVORBIS, DRFLAC, VOC, AIFF, AU, DRMP3, SINEWAVE, RAW` on every platform.
- **Decoding.** WAV, Ogg Vorbis, FLAC and MP3 from memory to f32 PCM, 44100 frames each (the source's length; dr_mp3 strips the encoder's delay and padding). WAV and FLAC equal the source bit for bit. Each format's output has the same SHA-256 on Linux and Windows, in JIT and NativeAOT.
- **Streaming.** The Ogg file through a headless mixer, a track and `MIX_Generate`, with one loop from frame 22050: the output is the one-shot decode bit for bit, then the decode from frame 22050 bit for bit, 66150 frames in all.
- **Allocation.** 0 managed bytes per `MIX_DecodeAudio` call and per `MIX_Generate` call after warm-up (`GC.GetAllocatedBytesForCurrentThread` over 44 and 68 calls), with BRUTAL's trace level off. The calls that take or return managed strings (`MIX_GetAudioDecoder`, `SDL_SetNumberProperty`'s property name, `SDL_GetError`) were not measured: they are on setup and error paths only.
- **NativeAOT:** 0 trim and AOT warnings on linux-x64 and win-x64, and the native libraries resolved from beside the executable.

**Not run:** macOS (best-effort and not in CI, [ADR-0015](0015-tested-platforms-windows-linux.md)); real audio output (the dummy driver plays nothing); resampling (every decode asked for the file's own rate); `loops = -1`, seeking, concurrent use of one decoder, files longer than a second, and native memory leak tracking.

## Consequences

- **A new dependency for M2.** The first `Audio.Sdl3` or importer issue adds the `Ahwoo.Brutal.Sdl.Mixer` version to `Directory.Packages.props`; it joins Dependabot's BRUTAL group with the other `Ahwoo.Brutal.*` packages. The ADR-0016 freeze holds: no 0.2.0 project resolves it, so no releasing lock file changes.
- **Licence notices.** Everything that ships `Audio.Sdl3` ships both packages' `LICENSES/BRUTAL-SDL-MIXER-THIRD-PARTY-NOTICES.txt`, copied under `LICENSES/<PackageId>/` as HelloWindow's licence target does for every BRUTAL package (`samples/HelloWindow/HelloWindow.csproj:52`, `:124`), because the managed and native packages ship different files under the same name. BRUTAL's own duties (logo, credits, NOTICE) already apply.
- **The ObjectPool closure.** `Ahwoo.Brutal.Sdl.Mixer` brings `Ahwoo.Brutal.Logging`, so `Ahwoo.Brutal.Strings` and `Microsoft.Extensions.ObjectPool`. `Audio.Sdl3` is an L2 backend and the importer an L6 tool, which [ADR-0011](0011-brutal-objectpool-dependency.md) already accepts, pinned to 11.0.0. `BrutalEngine.Audio` takes no BRUTAL binding package: when its project is created, it joins the modules that `PackageClosureRule` guards ([Architecture tests](../design/architecture-tests.md#rules)).
- **NativeAOT coverage.** BRUTAL declares no `IsAotCompatible`, so every decode path the engine uses (a one-shot decode per format, a stream with a loop) gets an entry in the NativeAOT smoke harness, with an allocation check on the per-call loop.
- **Seek and loop go through tracks**, so each stream costs a headless mixer. Its native memory wasn't measured.
- **Reproducible cooks.** The lossy decoders gave the same bytes on Linux and Windows for these files and this SDL_mixer version, so cooked PCM can be compared across cook hosts. Whether CI re-cooks audio and compares bytes, as it does for shaders ([ADR-0012](0012-m1-shader-cooking.md)), is for #281.
- **Docs updated with this ADR:** [Tech stack](../02-tech-stack.md#brutal-packages-used) (the package row and the "Not used" sentence), [Architecture 3.11](../03-architecture.md#311-audio-brutalengineaudio-backend-audiosdl3) (Sources), [Risks](../10-risks.md) (R13 and the S6 schedule row) and the [S6 results](../spikes/README.md#s6-compressed-audio-decoding-prototype). ADR-0004 is unchanged: its rule stands, and this ADR answers its open consequence.
- **Revisit if:**
  - a game needs Opus (first candidate: Concentus, managed, BSD-3; otherwise a native libopus under option 3);
  - a decoder bug blocks us and BRUTAL won't fix it;
  - a BRUTAL update makes a per-call decode or generate path allocate (the smoke harness's allocation check fails);
  - BRUTAL drops `Ahwoo.Brutal.Sdl.Mixer` or changes its decoder or headless-mixer API;
  - macOS support returns ([ADR-0015](0015-tested-platforms-windows-linux.md)): these paths have never run there.
