#!/usr/bin/env bash
# Regenerates the spike's test audio from scratch. Run from this directory.
# The committed files were made on Arch Linux with: Python 3, ffmpeg n9.1 (libvorbis encoder), flac 1.5.0, LAME 4.0.
# Re-encoding with other versions can give different bytes; the committed files are the test inputs.
set -euo pipefail
python3 make-tone.py tone.wav
ffmpeg -hide_banner -loglevel error -y -i tone.wav -map_metadata -1 -fflags +bitexact -flags:a +bitexact \
  -c:a libvorbis -q:a 4 tone.ogg
flac --silent --force --best --no-padding -o tone.flac tone.wav
lame --silent -b 128 tone.wav tone.mp3
sha256sum tone.wav tone.ogg tone.flac tone.mp3
