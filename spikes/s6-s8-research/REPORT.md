# Spikes S6 (compressed audio decoding) and S8 (complex-script shaping): research report

Date: 2026-11-25. Scope: BrutalEngine docs/03-architecture.md §3.11 and §3.12, docs/10-risks.md R7 and R13.
Constraints applied: .NET 10, NativeAOT, no reflection, zero managed allocations per frame, the audio callback never allocates, locks or blocks, natives only for win-x64, linux-x64 and osx-arm64, MIT engine.

Legend: **[V]** = I checked it myself, in a package binary, a BRUTAL source file or an upstream source file at a pinned tag. **[W]** = taken from a web page or README and not checked independently.

Nothing from the downloads was executed. Each nupkg went into its own empty directory under `dl/`. The packages were read with `python -I` scripts kept in `tools/` (zipfile, a minimal PE export/import parser, an ELF `.dynsym` parser, string scans). Raw outputs are in `evidence/`.

---

## TL;DR

- **S6 recommendation: decode Ogg Vorbis with SDL3_mixer's decoders, which are already in BRUTAL.**
  - Run a headless `MIX_CreateMixer(spec)`, one `MIX_Track` per streamed music voice, and pull PCM with `MIX_Generate` on our own streaming worker thread into a lock-free ring buffer that our mixer reads.
  - For one-shot or cooker decodes, use `MIX_CreateAudioDecoder_IO` with `MIX_DecodeAudio`.
  - This adds no natives, no licence work (stb_vorbis, dr_flac and dr_mp3 are already attributed by BRUTAL) and no AOT risk. FLAC, MP3 and WAV come for free.
  - **Opus is not in BRUTAL's build.** Drop Opus from v1: the cooker emits Vorbis for streamed music. If Opus becomes a requirement, add it behind the same `IStreamDecoder` interface. The first choice is Concentus 2.2.x (managed, BSD-3, active). The second is bundling libopus plus opusfile in our own native CI.
- **S8 recommendation: call HarfBuzz's C API through our own `[LibraryImport]` bindings (about 25 functions), feeding glyph ids and 26.6 positions into our own FreeType bitmap/SDF atlas.**
  - Load the native from the MIT `HarfBuzzSharp.NativeAssets.{Win32,Linux,macOS}` packages (`libHarfBuzzSharp`, which exports plain `hb_*`). Skip the managed HarfBuzzSharp wrapper.
  - The medium-term goal is to build `harfbuzz.cc` in our own native CI, which removes the third-party binary dependency.
  - BRUTAL's SDL3_ttf does contain HarfBuzz 8.5.0, but does not export `hb_*`. Reaching it means using SDL_ttf's own layout through a custom `TTF_TextEngine`: integer pixel positions, one direction/script per text and no BiDi. Keep that only as a fallback or prototype path.
  - Ahwoo.Brutal.FreeType does **not** ship HarfBuzz.

---

## S6: compressed audio decoding

### (a) SDL3_mixer via Ahwoo.Brutal.Sdl.Mixer: verified evidence

Package: `Ahwoo.BRUTAL.Sdl.Mixer.Native` 2026.10.3 (the only version on nuget.org; index `https://api.nuget.org/v3-flatcontainer/ahwoo.brutal.sdl.mixer.native/index.json`). Full output: `evidence/mixer-native.txt`, `evidence/mixer-linux-dynsym.txt`.

Files [V]:
```
 404464  runtimes/linux-x64/native/libSDL3_mixer.so(.0, .0.2.4)
 341176  runtimes/osx-arm64/native/libSDL3_mixer(.0, .0.2.4).dylib
 280576  runtimes/win-x64/native/SDL3_mixer.dll
  22380  build/natives/lib/win-x64/SDL3_mixer.lib
  12189  LICENSES/BRUTAL-SDL-MIXER-THIRD-PARTY-NOTICES.txt
```

Linked libraries [V]:
- Windows imports: `SDL3.dll, VCRUNTIME140.dll, api-ms-win-crt-runtime-l1-1-0.dll, KERNEL32.dll`.
- Linux DT_NEEDED: `libSDL3.so.0, libc.so.6`.
- There is no libvorbis, libogg, libopus, libFLAC, libmpg123 or libxmp, either as a dependency or bundled.

Decoders compiled in [V], from the `MIX_Decoder_*` symbols in the unstripped Linux .so:
`MIX_Decoder_AIFF, _AU, _DRFLAC, _DRMP3, _RAW, _SINEWAVE, _STBVORBIS, _VOC, _WAV`.
The win-x64 DLL has the same decoder name strings (`AIFF AU DRFLAC DRMP3 RAW SINEWAVE STBVORBIS VOC WAV`).
- `decoder_opus.c`, `decoder_xmp.c`, `decoder_wavpack.c` and `decoder_mpg123.c` appear only as source-file name strings on Linux. There are no `MIX_Decoder_OPUS`/`XMP` symbols, so those units are compiled out.
- The notices file agrees. BRUTAL/Brutal.Sdl.Mixer/LICENSES/BRUTAL-SDL-MIXER-THIRD-PARTY-NOTICES.txt:12 says `SDL_mixer 3.2.4`. Lines 17-19 list `stb_vorbis / dr_flac / dr_mp3`. Lines 26-28 say: "can optionally be built against libmpg123, libFLAC, libvorbis, libopus, libxmp, libgme, WavPack and FluidSynth. This build enables none of them".

Exports [V]:
- Win DLL: 94 exports, all `MIX_*`.
- Linux `.dynsym`: 94 `MIX_*` plus 0 `stb_*`/`drflac*`/`drmp3*`.
- So the inner stb_vorbis and dr_libs are **not callable directly**. Only the MIX API is.
- The relevant exports: `MIX_CreateAudioDecoder, MIX_CreateAudioDecoder_IO, MIX_DecodeAudio, MIX_GetAudioDecoderFormat, MIX_DestroyAudioDecoder, MIX_CreateMixer, MIX_Generate, MIX_SetTrackPlaybackPosition, MIX_SetTrackLoops, MIX_LoadAudioNoCopy, MIX_SetTrackIOStream`.

Bindings [V] (BRUTAL source, generated):
- BRUTAL/Brutal.Sdl.Mixer/Generated/SdlMixer.PInvoke.gen.cs:
  - :1003-1016 `CreateAudioDecoder(string path, uint props)` (UTF-8 custom marshaller)
  - :1018-1026 `CreateAudioDecoderIO(nint io, byte closeio, uint props)`
  - :1028-1036 `DestroyAudioDecoder`
  - :1048-1056 `GetAudioDecoderFormat(dec, SdlAudioSpec*)`
  - :1058-1071 `DecodeAudio(dec, void* buffer, int buflen, SdlAudioSpec* spec)`
- Headless mixer path, same file:
  - :76 `MIX_CreateMixer(SdlAudioSpec*)`
  - :993-1001 `MIX_Generate(mixer, void*, int)`
  - :283 `CreateTrack`, :323 `SetTrackAudio`, :343 `SetTrackIOStream`, :418 `SetTrackPlaybackPosition`, :458 `SetTrackLoops`, :558 `PlayTrack`, :136 `LoadAudio_IO`, :161 `LoadAudioNoCopy`.
- Handle wrapper: Generated/SdlMixer.HandleMethods.gen.cs:214-282 (`MixAudioDecoder.DecodeAudio/Destroy/GetFormat`). The doc comment at :228-236 reads: "Data is decoded on demand in whatever format is requested… A return value of zero means the entire file was decoded". :239 adds "safe to call from any thread".
- Loop-point properties are bound: Generated/SdlMixer.Constants.gen.cs:37 `PropPlayLoopStartFrameNumber = "SDL_mixer.play.loop_start_frame"`. The upstream header also has `MIX_PROP_PLAY_START_FRAME_NUMBER`, `MAX_FRAME`, `LOOPS` and `FADE_IN_*`.
- In-memory IO for packed assets: BRUTAL/Brutal.Sdl/Generated/Sdl.PInvoke.gen.cs:2916 `SDL_IOFromConstMem(void*, nuint)`, :2936 `SDL_OpenIO(SdlIOStreamInterface*, void*)`. The second lets us stream from our own pak/VFS.
- Per-call overhead: every generated wrapper calls `SdlMixer.TraceLog.Trace()` (Brutal.Sdl.Mixer/SdlMixer.cs:20-23). String parameters use a 256-byte stack buffer and fall back to `NativeMemory.Alloc`, never the managed heap (Brutal.Sdl/CustomStringMarshaller.cs:22-61). Both are fine off the audio thread. If profiling shows Trace cost, we can declare our own `[LibraryImport("SDL3_mixer")]` for the 3-4 hot functions.

Upstream API semantics [V] (SDL_mixer `release-3.2.4`, include/SDL3_mixer/SDL_mixer.h and src/SDL_mixer.c):
- The decoder API is exactly `MIX_GetNumAudioDecoders, MIX_GetAudioDecoder, MIX_CreateAudioDecoder(_IO), MIX_DestroyAudioDecoder, MIX_GetAudioDecoderProperties, MIX_GetAudioDecoderFormat, MIX_DecodeAudio`. **There is no seek or rewind on `MIX_AudioDecoder`.**
- `MIX_DecodeAudio` (SDL_mixer.c) does `SDL_SetAudioStreamFormat(stream, NULL, spec)`, loops `decoder->decode(...)` into an internal `SDL_AudioStream` until `buflen` is available, then `SDL_GetAudioStreamData`. Output can therefore be requested directly as F32 at our mix rate and channel count, with SDL doing the resampling. Any memory it allocates is native SDL memory, not managed.
- `MIX_CreateMixer(spec)` creates "a mixer that generates audio to a memory buffer… used with MIX_Generate() to produce more data on demand" and is "safe to call from any thread". `MIX_Generate` "can not be used with mixers from MIX_CreateMixerDevice()".
- Tracks on such a mixer support seeking (`MIX_SetTrackPlaybackPosition`), loop counts and `loop_start_frame`. That covers intro+loop music.

### (b) Managed decoders

| | NVorbis | Concentus (+ Concentus.OggFile) |
|---|---|---|
| Codec | Vorbis only | Opus (+ Ogg Opus container via OggFile) |
| Latest | 1.0.0-rc.2 (2026-07-13, **pre-release**); last stable 0.10.5 (2022-10-16) [V nuget registration] | 2.2.3 (2026-11-17, stable); OggFile 1.0.7 (2026-03-17) [V] |
| Licence | MIT [V GitHub API + nuspec `license type=file`] | BSD-3-style (libopus terms; LICENSE lists Skype, Xiph, CSIRO, Microsoft…, Stromberg) [V LICENSE text]; OggFile MIT [V nuspec] |
| Repo activity | NVorbis/NVorbis pushed 2026-07-17, 120★, not archived [V] | lostromb/concentus pushed 2026-11-17, 366★ [V] |
| TFMs | netstandard2.0/2.1 only (80 KB) [V] | net8.0, netstandard2.0/1.1, net452 (≈398 KB) [V] |
| Trim/AOT markers | no `IsTrimmable`/`IsAotCompatible` metadata strings found [V string scan] | net8.0 build has `IsTrimmable` and uses `LibraryImport` [V string scan] |
| Reflection/locks in metadata | references `ArrayPool`, `Monitor`/`_readLock`, `ConcurrentDictionary`, `System.Threading.Tasks`, `System.Linq` [V string scan] | `OpusCodecFactory`, `NativeOpusDecoder`, `AttemptToUseNativeLibrary`, `NativeLibraryAvailable` [V]: it optionally P/Invokes a system libopus |
| Performance | not measured | README: "about 40-50% as fast as its equivalent libopus build"; Pi 1 benchmarks 0.98x realtime for 48 kHz stereo 128 kbps CELT [W] |
| Allocation | ReadSamples(Span<float>) exists (per release notes) [W]; internal ArrayPool use and locks per read [V names only, behaviour W] | OggFile `OpusOggReadStream.DecodeNextPacket` [V name]; it is reported to return a new `short[]` per packet [W, unverified], so we would need our own Ogg demux plus `Decode(span…)` |

Notes:
- Both would run on our streaming thread, not the audio callback, so locks inside them are tolerable. Per-packet allocations are not tolerable under the zero-alloc rule.
- NVorbis duplicates what SDL3_mixer already gives us natively (stb_vorbis), and its 1.0 is still a release candidate.
- The real value of Concentus is Opus, which nothing in BRUTAL provides.

### (c) Bundling C decoders ourselves

| Library | Licence | Build cost per RID | Notes |
|---|---|---|---|
| stb_vorbis | MIT or Unlicense (BRUTAL already reproduces it: Brutal.Sdl.Mixer notices :60-110) [V] | single .c file; trivial on win-x64/linux-x64/osx-arm64 | The same code already ships inside SDL3_mixer but is not exported [V], so bundling would duplicate it. It does offer a real pull API with seek (`stb_vorbis_get_samples_float_interleaved`, `stb_vorbis_seek`) and a user-supplied alloc buffer (`stb_vorbis_alloc`) [W, stb docs]. |
| dr_flac / dr_mp3 | Unlicense or MIT-0 (notices :111-240) [V] | single header each | Already in SDL3_mixer [V]. |
| libopus + libogg + opusfile | BSD-3 (xiph/opus COPYING [V]; opusfile BSD-3-Clause [V GitHub API]) | real CMake build, 3 libraries, SIMD options; needs a native CI matrix for win-x64, linux-x64 and osx-arm64 | Fastest and most correct Opus; the only native Opus route. |

Bundling anything means owning a native build pipeline and NOTICE entries. BRUTAL avoids that for Vorbis, FLAC and MP3 but not for Opus.

### S6 comparison

| Criterion | SDL3_mixer (BRUTAL) | NVorbis | Concentus | Bundle stb_vorbis / libopus |
|---|---|---|---|---|
| Vorbis | yes [V] | yes | no | yes |
| Opus | **no** [V] | no | yes | yes (libopus) |
| FLAC / MP3 | yes [V] | no | no | with dr_libs |
| New natives / CI | none | none | none (optional native libopus) | yes, per RID |
| NativeAOT | LibraryImport, already in BRUTAL [V] | unannotated netstandard; must be tested | IsTrimmable on net8.0 [V]; must be tested | trivially (our own LibraryImport) |
| Managed allocs per frame | none from the decoder; our worker passes a pinned/native buffer | likely low but unverified | Ogg layer likely allocates [W] | none |
| Seek / loop points | via headless MIX_Mixer + track props [V]; MIX_AudioDecoder has no seek [V] | yes | via OggFile `SeekTo*` [V names] | yes |
| Licence load | already in BRUTAL NOTICE | MIT | BSD-3 attribution | MIT/Unlicense, BSD-3 |

### S6 recommendation

1. **v1: SDL3_mixer as the decoder library, our mixer as the mixer.** Define `IStreamDecoder { Format; int Read(Span<float> dst); bool Seek(long frame); }`. Implement it with one headless `MIX_CreateMixer(F32, ourRate, ch)` per streaming voice, plus `MIX_LoadAudio_IO` or `MIX_SetTrackIOStream` (`predecode=false`) over `SDL_IOFromConstMem` or our own `SDL_OpenIO` VFS stream, plus `MIX_PlayTrack` with `loops`/`loop_start_frame`, and pull with `MIX_Generate` on the streaming worker into an SPSC ring buffer.
   - The audio thread only reads the ring buffer, so it never touches SDL_mixer and the "no alloc/lock/block in the callback" rule (§3.11) holds.
   - Cooker and short-sound path: `MIX_CreateAudioDecoder_IO` + `MIX_DecodeAudio` to PCM.
   - Gate this in the S6 spike with an AOT publish on all three RIDs, a decode-throughput benchmark, and `GC.GetAllocatedBytesForCurrentThread()` == 0 across 10k pulls.
2. **Opus: not in v1.** The cooker transcodes music to Vorbis q4-q6. If Opus is required later (voice chat, smaller music), try Concentus 2.2.x first. Use its `OpusDecoder` directly with our own Ogg page parser so there are no per-packet arrays, and allow `AttemptToUseNativeLibrary` if we bundle libopus. If perf or allocations fail, bundle libopus+opusfile in a native CI.
3. Don't use NVorbis: it is redundant with stb_vorbis-in-SDL_mixer, still pre-1.0, and adds unannotated locks.
4. Risk to note: SDL_mixer's headless-mixer path makes SDL_mixer part of our streaming chain, so SDL_mixer bugs and behaviour changes affect us. The fallback is to bundle stb_vorbis (one C file) with our own LibraryImport.

---

## S8: complex-script shaping

### Ahwoo.Brutal.Sdl.Ttf.Native 2026.10.3: verified evidence

Full output: `evidence/ttf-native.txt`, `evidence/ttf-linux-dynsym.txt`.

Files [V]:
- `runtimes/win-x64/native/SDL3_ttf.dll` (1,593,344 B)
- `runtimes/linux-x64/native/libSDL3_ttf.so.0.2.2` (2,368,696 B)
- `runtimes/osx-arm64/native/libSDL3_ttf.0.2.2.dylib` (2,056,184 B)
- `build/natives/lib/win-x64/SDL3_ttf.lib`

**HarfBuzz is statically linked** [V]:
- The binaries contain `hb_shape`, `hb_shape_full`, `hb_shape_plan_create`, Indic/USE/Khmer/Myanmar syllable finders (`find_syllables_indic`, `find_syllables_khmer`…), and the strings "Cannot create harfbuzz buffer" and `TTF_GetHarfBuzzVersion`.
- Notices: BRUTAL/Brutal.Sdl.Ttf/LICENSES/BRUTAL-SDL-TTF-THIRD-PARTY-NOTICES.txt:12 `SDL_ttf 3.2.2`, :17 `FreeType 2.13.2`, :20 `zlib 1.3`, :21 `HarfBuzz 8.5.0`, :22 `stb_rect_pack`.

**PlutoSVG is not included** [V]: no `plutosvg` strings, and notices :53-55 say "This build enables neither". So there are no colour-emoji SVG glyphs.

**FreeType is statically linked** (FT_Init_FreeType present) [V].

Imports and dependencies [V]:
- win-x64 imports: `SDL3.dll, GDI32, USP10, RPCRT4, KERNEL32, USER32, VCRUNTIME140(_1), CRT`.
- Linux DT_NEEDED: `libSDL3.so.0, libm, libgcc_s, libc`.

**Exports** [V]:
- Win: 117 exports, all `TTF_*`.
- Linux `.dynsym`: 117 `TTF_*`, **0 `hb_*`, 0 `FT_*`**.
- So HarfBuzz and FreeType inside SDL3_ttf cannot be P/Invoked directly on win-x64 or linux-x64. osx-arm64 Mach-O exports were not parsed (unverified), but are presumably the same.

Shaping-related exports and bindings [V]. All are in BRUTAL/Brutal.Sdl.Ttf/Generated/SdlTtf.PInvoke.gen.cs:
- :31 `TTF_GetHarfBuzzVersion`
- :426 `SetFontDirection(TtfFont, TtfDirection)`
- :448 `StringToTag(string)`
- :471 `SetFontScript(TtfFont, uint script)`
- :491 `GetGlyphScript(uint ch)`
- :503 `SetFontLanguage(TtfFont, string bcp47)`
- :536 `GetGlyphImageForIndex(font, glyph_index, TtfImageType*)`
- :256 `SetFontSDF`
- :1024 `CreateText(TtfTextEngine* engine, TtfFont, string, nuint)`
- :1087 `SetTextDirection`, :1107 `SetTextScript`
- :1292 `GetTextSubString`, :1312 `GetTextSubStringsForRange`, :1351 `GetNextTextSubString`
- :1365 `UpdateText`
- :889 `CreateSurfaceTextEngine`, :959 `CreateGPUTextEngine` (SDL_GPU only, so not usable with our Vulkan renderer), :979 `GetGPUTextDrawData`

Enum: Generated/Enums/TtfDirection.gen.cs (`Ltr=4, Rtl=5, Ttb=6, Btt=7`, the HarfBuzz values).

**Custom text engine structs are bound** [V], in Generated/SdlTtf.Structs.gen.cs:
- :8-14 `TtfTextEngine { uint version; void* userdata; delegate* unmanaged[Cdecl]<void*, TtfText*, byte> CreateText; …DestroyText; }`
- :17-35 `TtfTextData { …; int num_ops; TtfDrawOperation* ops; int num_clusters; TtfSubString* clusters; … }`
- :78-87 `TtfCopyOperation { cmd; int text_offset; TtfFont glyph_font; uint glyph_index; SdlRect src; SdlRect dst; void* reserved; }`
- Upstream SDL_textengine.h (release-3.2.2) documents `glyph_index` as "can be passed to TTF_GetGlyphImageForIndex()". `CreateText` receives a text with "all fields … already filled out" [V].

How SDL_ttf shapes (upstream src/SDL_ttf.c at release-3.2.2) [V]:
- `CollectGlyphsFromFont`, lines 3311-3377, does `hb_buffer_create` **per call**, sets **one** language, direction and script for the whole string (:3322-3324), runs `hb_buffer_guess_segment_properties`, and calls `hb_shape(font->hb_font, …, kern feature)` (:3336).
- It copies `codepoint` → glyph index, x/y advance and offset (26.6) and `cluster` (:3364-3372), then loads each glyph through SDL_ttf's glyph cache (`Find_GlyphByIndex`, :3373).
- `hb_font` comes from `hb_ft_font_create(face)` (:2156).
- The draw ops it emits have **integer pixel** `dst` rects (:1466-1478).
- I saw no UAX #9 BiDi run splitting in this path: one direction per `TTF_Text`. I didn't audit the whole file, so mixed-direction handling elsewhere is unverified.

So SDL_ttf can give us shaped glyph runs without rendering surfaces. Use `TTF_CreateText` with our own `TtfTextEngine` (CreateText callback via `[UnmanagedCallersOnly]`), or `engine=NULL` plus `TTF_UpdateText`, then read `text->internal->ops[]` for glyph_index, dst and text_offset, plus `clusters[]`. Limits:
- Positions are rounded to whole pixels at one point size. That suits bitmap fonts and hurts SDF text at scale.
- There is no BiDi itemisation, so we would still split runs by direction and script ourselves and create one TTF_Text per run.
- It uses a separate FreeType (2.13.2) from Ahwoo.Brutal.FreeType (2.14.3). The glyph ids still match because both read the same font file and face index, but SDL_ttf also loads every glyph itself (double work).
- It allocates per shape call natively (hb_buffer_create). It needs a `TTF_Font` per size.
- It depends on reading `TtfTextData` internals (versioned `TTF_TextEngine.version` interface, part of the public SDL_textengine.h, so semi-stable).

### Ahwoo.Brutal.FreeType 2026.10.2 [V]

No `.Native` package exists: the `index.json` for `ahwoo.brutal.freetype.native` returns BlobNotFound. The natives are inside `Ahwoo.BRUTAL.FreeType` itself:
- `runtimes/win-x64/native/freetype.dll` (684,032 B; 203 `FT_*` exports) and `png18.dll`
- `runtimes/linux-x64/native/libfreetype.so.6.20.6` and `libpng18.so.18`
- osx-arm64 dylibs
- FreeType public headers under `build/natives/include/freetype/`

What it does and doesn't contain:
- **No HarfBuzz**: 0 `hb_shape`/`harfbuzz` strings in any binary.
- The SDF renderer is compiled in (`ft_sdf_*`, `bsdf_*`).
- Notices: Brutal.FreeType/LICENSES/BRUTAL-FREETYPE-THIRD-PARTY-NOTICES.txt:10 `FreeType 2.14.3`.
- The managed `lib/net10.0/Brutal.FreeType.dll` is 5,120 B and has no binding types (metadata strings show only assembly attributes). The BRUTAL repo folder Brutal.FreeType/ holds only LICENSES/. **BRUTAL does not bind the FreeType API.** We write our own `[LibraryImport("freetype")]` for FT_* (needed anyway for §3.12).

### HarfBuzzSharp (NuGet) [V unless marked]

- `HarfBuzzSharp` 14.4.0.200: latest stable, published 2026-11-18. There is also 15.0.0.100-rc.1. Licence MIT (nuspec expression). Source mono/SkiaSharp, which is active (pushed 2026-11-24, 5.6k★).
- Managed `lib/net10.0/HarfBuzzSharp.dll` (144 KB): metadata contains `IsTrimmable`, `LibraryImportAttribute`, `DisableRuntimeMarshalling`, `UnmanagedCallersOnly`, `ArrayPool`. No `IsAotCompatible` string was found.
- The managed API is object-based (Buffer, Font, Face classes). Whether its glyph-info/position accessors avoid allocations is unverified [W].
- Natives, from `evidence/hbsharp-natives.txt`:
  - win-x64 2.19 MB, win-arm64, win-x86
  - linux-x64 3.08 MB, linux-arm64, musl-x64/arm64 and others
  - osx universal `runtimes/osx/native/libHarfBuzzSharp.dylib` 3.12 MB
  - So it covers more RIDs than BRUTAL (osx-x64, linux-arm64, win-arm64).
- `libHarfBuzzSharp.dll` (win-x64) imports only `KERNEL32.dll` (static CRT) and exports the plain C API: 565 exports, including `hb_blob_create, hb_face_create, hb_font_create, hb_buffer_*, hb_buffer_guess_segment_properties, hb_buffer_set_direction, hb_shape, hb_shape_full, hb_buffer_get_glyph_infos, hb_buffer_get_glyph_positions, hb_version_string` (`evidence/hbsharp-win-x64-exports.txt`).
  - It has **no `hb_ft_*`**, so we build the face from the font bytes (`hb_blob_create` → `hb_face_create`) and use HarfBuzz's own OT font functions.
  - The glyph ids equal FreeType's for the same face index.
- The exact HarfBuzz version inside wasn't read (I'd need to run `hb_version_string`).

### Bundling HarfBuzz ourselves

- Licence: "Old MIT" (harfbuzz/harfbuzz COPYING [V]). Attribution goes into our NOTICE.
- Build: HarfBuzz ships an amalgamated `src/harfbuzz.cc` single translation unit [W, upstream build docs]. That makes a C++ static or shared library about 1.5-3 MB per RID; the measured sizes are SDL3_ttf.dll at 1.59 MB including FreeType and HarfBuzz, and libHarfBuzzSharp at 2.2-3.1 MB [V].
- Cost: a native CI job per RID (MSVC, GCC, Apple clang). That is cheap if we already run a native CI for other things, and a new pipeline otherwise.

### S8 comparison

| Criterion | SDL3_ttf (BRUTAL) custom text engine | HarfBuzzSharp managed wrapper | **HarfBuzz C API via own LibraryImport on libHarfBuzzSharp** | Bundle HarfBuzz ourselves |
|---|---|---|---|---|
| Available now | yes, 3 RIDs [V] | yes | yes | needs CI |
| Output | glyph_index + **int** dst rects + clusters [V] | glyph infos + 26.6 positions | glyph infos + 26.6 positions [V exports] | same |
| Direction/script/language | one per TTF_Text [V] | per buffer | per buffer (our itemiser) | same |
| Works with our FreeType/SDF atlas | yes, but double glyph loading | yes | yes | yes |
| AOT | LibraryImport [V] | IsTrimmable + LibraryImport [V]; class-based API | trivially AOT (blittable P/Invoke) | trivially |
| Per-frame managed allocs | none if we cache; native malloc per shape | wrapper objects; likely allocations [W] | none (reuse hb_buffer, read spans of native arrays) | none |
| RIDs | win-x64, linux-x64, osx-arm64 | many | many [V] | what we build |
| Licence | already in BRUTAL notices | MIT (wrapper + HarfBuzz Old MIT) | MIT + HarfBuzz Old MIT | HarfBuzz Old MIT |
| Size | 0 extra | +2-3 MB per RID + 144 KB | +2-3 MB per RID | ~1.5-3 MB per RID |
| Coupling risk | SDL_ttf internals (TtfTextData) | SkiaSharp release cadence | SkiaSharp cadence; native name `libHarfBuzzSharp` | ours |

### S8 recommendation

1. Use HarfBuzz directly through a small hand-written `[LibraryImport]` layer in `BrutalEngine.Text`, about 25 functions:
   - setup: `hb_blob_create`, `hb_face_create`, `hb_font_create`, `hb_font_set_scale`
   - per shape: `hb_buffer_create`/`reset`/`add_utf8|utf16`, `set_direction`/`script`/`language`, `hb_shape`, `get_glyph_infos`/`positions`, `hb_*_destroy`
   - Reuse one `hb_buffer_t` per layout thread, so the steady state allocates nothing on the managed heap.
   - Our layout code does BiDi (UAX #9) and script itemisation plus fallback chains, then feeds glyph ids into our FreeType bitmap/SDF atlas.
   - Ship the native from `HarfBuzzSharp.NativeAssets.Win32/Linux/macOS` 14.4.0.200 (MIT), pinned, using only `runtimes/{win-x64,linux-x64,osx}`. Don't reference the managed `HarfBuzzSharp` package.
   - Medium term (before 1.0 or when the native CI exists), build `harfbuzz.cc` per RID ourselves under a stable library name, so the engine isn't tied to SkiaSharp's binary naming and cadence. The binding stays the same.
2. The SDL3_ttf custom-text-engine route is valid for a quick S8 prototype or as a fallback. It is verified possible with the existing bindings. It isn't the production path because positions are integer pixels, there is one direction/script per text, rasterisation is duplicated in a second FreeType, and it reads SDL_ttf internals.
3. Update R7 to say HarfBuzz is present in BRUTAL's SDL3_ttf but not exported, so it can only be used through SDL_ttf's layout. The mitigation is direct HarfBuzz via libHarfBuzzSharp, and then our own build.

---

## Open items to verify in the actual spikes (not done here)

- Run (not just read) the following: AOT publish on win-x64, linux-x64 and osx-arm64 of (i) headless MIX_Mixer + MIX_Generate streaming a looping Ogg with a `loop_start_frame`, (ii) `hb_shape` via LibraryImport on Arabic, Hebrew and Devanagari samples.
- Measure managed allocations (`GC.GetAllocatedBytesForCurrentThread`) and decode CPU per minute of 48 kHz stereo audio.
- Check that libHarfBuzzSharp.dylib (universal) loads under our osx-arm64 NativeAOT host, and read its `hb_version_string`.
- Parse the osx-arm64 Mach-O export tries for SDL3_ttf/SDL3_mixer (only Windows and Linux exports were verified).
- Concentus: confirm the `Decode(ReadOnlySpan<byte>, Span<float>, …)` overload and that it doesn't allocate, if Opus is ever reinstated.

## Sources consulted

- BRUTAL (read-only): Brutal.Sdl.Mixer/{Generated/SdlMixer.PInvoke.gen.cs, Generated/SdlMixer.HandleMethods.gen.cs, Generated/SdlMixer.Constants.gen.cs, SdlMixer.cs, LICENSES/*}; Brutal.Sdl.Ttf/{Generated/SdlTtf.PInvoke.gen.cs, Generated/SdlTtf.Structs.gen.cs, Generated/Enums/*, LICENSES/*}; Brutal.Sdl/{CustomStringMarshaller.cs, Generated/Sdl.PInvoke.gen.cs}; Brutal.FreeType/LICENSES/*; brutal-engine docs/03-architecture.md:167-180, docs/10-risks.md:13,19.
- NuGet: https://api.nuget.org/v3-flatcontainer/{ahwoo.brutal.sdl.mixer.native, ahwoo.brutal.sdl.ttf.native, ahwoo.brutal.freetype, ahwoo.brutal.sdl.mixer, ahwoo.brutal.sdl.ttf, nvorbis, concentus, concentus.oggfile, harfbuzzsharp, harfbuzzsharp.nativeassets.{linux,win32,macos}}/…; https://api.nuget.org/v3/registration5-gz-semver2/<id>/index.json
- Upstream source: https://raw.githubusercontent.com/libsdl-org/SDL_mixer/release-3.2.4/include/SDL3_mixer/SDL_mixer.h, …/release-3.2.4/src/SDL_mixer.c, https://raw.githubusercontent.com/libsdl-org/SDL_ttf/release-3.2.2/include/SDL3_ttf/SDL_textengine.h, …/SDL_ttf.h, …/src/SDL_ttf.c
- READMEs and licences: NVorbis/NVorbis README + releases API; lostromb/concentus README + LICENSE + releases; xiph/opus COPYING; harfbuzz/harfbuzz COPYING; GitHub repo API for activity (NVorbis, concentus, SkiaSharp, harfbuzz, opus, opusfile, dr_libs, stb, SDL_mixer, SDL_ttf).
