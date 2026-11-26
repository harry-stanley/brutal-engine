#!/usr/bin/env python3
"""Writes tone.wav: a generated test signal, so the spike needs no third-party audio.

1.0 s, 44100 Hz, 2 channels, 16-bit PCM. Left: 440 Hz sine, right: 660 Hz sine, both at amplitude 0.5,
with 10 ms linear fades at each end. Deterministic: the same bytes on every run.
The encoded copies (tone.ogg, tone.flac, tone.mp3) are made from it by encode.sh.
"""
import math
import struct
import sys

RATE = 44100
FRAMES = RATE  # 1.0 s
FADE = RATE // 100  # 10 ms


def sample(freq: float, i: int) -> int:
    gain = min(1.0, i / FADE, (FRAMES - 1 - i) / FADE)
    value = 0.5 * gain * math.sin(2.0 * math.pi * freq * i / RATE)
    return int(round(value * 32767.0))


def main(path: str) -> None:
    data = bytearray()
    for i in range(FRAMES):
        data += struct.pack("<hh", sample(440.0, i), sample(660.0, i))
    header = b"RIFF" + struct.pack("<I", 36 + len(data)) + b"WAVE"
    header += b"fmt " + struct.pack("<IHHIIHH", 16, 1, 2, RATE, RATE * 4, 4, 16)
    header += b"data" + struct.pack("<I", len(data))
    with open(path, "wb") as f:
        f.write(header + data)


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "tone.wav")
