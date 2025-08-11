#!/usr/bin/env python3
"""
Worker script: reads an input audio file, detects notes, and writes outputs:
- JSON notes to output_json
- MusicXML score to output_musicxml

Usage:
  python process.py --input path/to/audio --instrument auto|alto|tenor|baritone|soprano \
                    --output-json notes.json --output-musicxml score.musicxml

Dependencies (see worker/requirements.txt): librosa, soundfile, music21
"""
import argparse
import json
from pathlib import Path

import numpy as np
import librosa
from music21 import stream as m21stream, tempo as m21tempo, meter as m21meter, note as m21note, instrument as m21inst


def estimate_f0(y: np.ndarray, sr: int) -> np.ndarray:
    f0, _, _ = librosa.pyin(y, fmin=librosa.note_to_hz("C2"), fmax=librosa.note_to_hz("C7"), sr=sr,
                             frame_length=2048, hop_length=256)
    return np.nan_to_num(f0, nan=0.0)


def hz_to_midi(hz: float) -> float:
    if hz <= 0:
        return np.nan
    return 69.0 + 12.0 * np.log2(hz / 440.0)


def midi_to_name(midi: int) -> str:
    m = m21note.Note(midi)
    return m.nameWithOctave


def transcribe(y: np.ndarray, sr: int, tempo: int = 120):
    hop = 256
    f0 = estimate_f0(y, sr)
    rmse = librosa.feature.rms(y=y, frame_length=2048, hop_length=hop)[0]
    rmse = (rmse - rmse.min()) / (rmse.ptp() + 1e-9)
    midi_cont = np.array([hz_to_midi(v) if v > 0 else np.nan for v in f0])
    # median filter
    size = 5
    half = size // 2
    midi_smooth = np.copy(midi_cont)
    for i in range(len(midi_cont)):
        s = max(0, i - half)
        e = min(len(midi_cont), i + half + 1)
        w = midi_cont[s:e]
        w = w[~np.isnan(w)]
        midi_smooth[i] = np.median(w) if len(w) else np.nan

    voiced = (rmse > 0.08) & (~np.isnan(midi_smooth))
    notes = []
    current_start = None
    current_pitch = None
    tol = 0.5
    for i, v in enumerate(voiced):
        if v:
            p = int(round(midi_smooth[i]))
            if current_start is None:
                current_start = i
                current_pitch = p
            else:
                if abs(p - int(current_pitch)) > tol:
                    s = current_start * hop / sr
                    e = i * hop / sr
                    if e - s > 0.04:
                        notes.append({"start": float(s), "end": float(e), "midi": int(current_pitch), "name": midi_to_name(int(current_pitch))})
                    current_start = i
                    current_pitch = p
        else:
            if current_start is not None and current_pitch is not None:
                s = current_start * hop / sr
                e = i * hop / sr
                if e - s > 0.04:
                    notes.append({"start": float(s), "end": float(e), "midi": int(current_pitch), "name": midi_to_name(int(current_pitch))})
                current_start = None
                current_pitch = None

    if current_start is not None and current_pitch is not None:
        s = current_start * hop / sr
        e = len(f0) * hop / sr
        notes.append({"start": float(s), "end": float(e), "midi": int(current_pitch), "name": midi_to_name(int(current_pitch))})
    return notes


def to_stream(notes, instrument: str, tempo: int = 120):
    s = m21stream.Stream()
    inst = {
        "alto": m21inst.AltoSaxophone,
        "tenor": m21inst.TenorSaxophone,
        "baritone": m21inst.BaritoneSaxophone,
        "soprano": m21inst.SopranoSaxophone,
    }.get(instrument, m21inst.SopranoSaxophone)
    s.append(inst())
    s.append(m21meter.TimeSignature("4/4"))
    s.append(m21tempo.MetronomeMark(number=tempo))
    for n in notes:
        d = max(0.25, (n["end"] - n["start"]) * (tempo / 60.0))
        m = m21note.Note(n["midi"]) 
        m.duration.quarterLength = d
        s.append(m)
    return s


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--input", required=True)
    ap.add_argument("--instrument", default="auto")
    ap.add_argument("--output-json", required=True)
    ap.add_argument("--output-musicxml", required=True)
    args = ap.parse_args()

    y, sr = librosa.load(args.input, sr=44100, mono=True)
    if np.max(np.abs(y)) > 0:
        y = y / np.max(np.abs(y))
    notes = transcribe(y, sr)

    Path(args.output_json).write_text(json.dumps({"notes": notes}), encoding="utf-8")
    s = to_stream(notes, instrument=args.instrument if args.instrument != "auto" else "soprano")
    s.write("musicxml", fp=args.output_musicxml)


if __name__ == "__main__":
    main()


