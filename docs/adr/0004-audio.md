# ADR-0004: Own mixer on SDL3 audio, FMOD as an optional later plugin

- **Status:** Accepted
- **Date:** 2026-11-25
- **Deciders:** Harry Stanley (maintainer)

## Context

Audio must work on all three OSes with no licence cost to users and full control over real-time behaviour (no allocation or locking on the audio thread). BRUTAL binds OpenAL Soft, SDL3 audio, SDL3_mixer and FMOD (FMOD natives not bundled, and FMOD has licence fees above budget thresholds).

## Options considered

1. **SDL3 audio + our mixer:** full control, one dependency already present, testable as a pure function. More engineering.
2. **OpenAL Soft:** a ready-made 3D-oriented mixer. Less control over buses/DSP, positional model tuned for 3D.
3. **FMOD primary:** best tooling. Closed source, licence fees, not bundled.

## Decision

SDL3 audio streams as the device layer, with an in-house real-time mixer behind an `IAudioEngine` abstraction. FMOD may be added after v1 as an optional backend.

## Consequences

- We own mixer quality and performance. Mixer tests render to memory and compare against reference buffers.
- Compressed streaming needs a decoder (spike S6, risk R13).
