#!/usr/bin/env python3
"""
Worker script: reads an input audio file, detects notes, and writes outputs:
- JSON notes + metadata to output_json
- MusicXML score to output_musicxml

Usage:
  python process.py --input path/to/audio --instrument auto|alto|tenor|baritone|soprano \
                    --output-json notes.json --output-musicxml score.musicxml [--tempo 120]

Dependencies (see worker/requirements.txt): librosa, soundfile, music21
Optional: aubio, demucs, spleeter
"""
import argparse
import json
from pathlib import Path

import numpy as np
import librosa
import librosa.effects
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


def bandpass_fft(y: np.ndarray, sr: int, low: float = 180, high: float = 2000) -> np.ndarray:
    Y = np.fft.rfft(y)
    freqs = np.fft.rfftfreq(len(y), 1.0 / sr)
    mask = (freqs >= low) & (freqs <= high)
    Yf = np.zeros_like(Y)
    Yf[mask] = Y[mask]
    out = np.fft.irfft(Yf, n=len(y))
    out = out / (np.max(np.abs(out)) + 1e-9)
    return out.astype(np.float32)


def isolate_sax(y: np.ndarray, sr: int, mode: str = "auto") -> np.ndarray:
    if mode == "no":
        return y
    # Try demucs
    if mode in ("auto", "demucs"):
        try:
            import torch  # type: ignore
            from demucs.pretrained import get_model  # type: ignore
            from demucs.apply import apply_model  # type: ignore
            model = get_model("htdemucs")
            wav = torch.tensor(y[None, None, :])
            sources = apply_model(model, wav, split=True, overlap=0.25)[0]  # (num_src, ch, T)
            # Combine other+vocals as proxy melodic
            # Demucs source order varies; pick the first two as a naive fallback
            mix = sources.mean(dim=1).detach().cpu().numpy()
            comp = mix[0]
            if mix.shape[0] > 1:
                comp = 0.5 * (mix[0] + mix[1])
            comp = comp / (np.max(np.abs(comp)) + 1e-9)
            return comp.astype(np.float32)
        except Exception:
            pass
    # Try spleeter
    if mode in ("auto", "spleeter"):
        try:
            from spleeter.separator import Separator  # type: ignore
            sep = Separator("spleeter:2stems")
            wav = np.stack([y, y], axis=1)
            pred = sep.separate(wav)
            comp = pred.get("other")
            if comp is None:
                comp = pred[list(pred.keys())[0]]
            comp = comp.mean(axis=1)
            comp = comp / (np.max(np.abs(comp)) + 1e-9)
            return comp.astype(np.float32)
        except Exception:
            pass
    # Fallback: HPSS + band-pass
    harm, _ = librosa.effects.hpss(y)
    return bandpass_fft(harm, sr)


def quantize_notes(notes, tempo: int, divisions: int = 16):
    if divisions <= 0:
        return notes
    grid = 60.0 / tempo / (divisions / 4.0)  # seconds per 1/divisions note
    q = []
    for n in notes:
        s = round(n["start"] / grid) * grid
        e = round(n["end"] / grid) * grid
        if e <= s:
            e = s + grid
        q.append({**n, "start": float(s), "end": float(e)})
    return q


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
                        vel = float(np.clip(np.median(rmse[current_start:i]) * 127.0, 1, 127))
                        notes.append({"start": float(s), "end": float(e), "midi": int(current_pitch), "name": midi_to_name(int(current_pitch)), "velocity": int(round(vel))})
                    current_start = i
                    current_pitch = p
        else:
            if current_start is not None and current_pitch is not None:
                s = current_start * hop / sr
                e = i * hop / sr
                if e - s > 0.04:
                    vel = float(np.clip(np.median(rmse[current_start:i]) * 127.0, 1, 127))
                    notes.append({"start": float(s), "end": float(e), "midi": int(current_pitch), "name": midi_to_name(int(current_pitch)), "velocity": int(round(vel))})
                current_start = None
                current_pitch = None

    if current_start is not None and current_pitch is not None:
        s = current_start * hop / sr
        e = len(f0) * hop / sr
        vel = float(np.clip(np.median(rmse[current_start:]) * 127.0, 1, 127))
        notes.append({"start": float(s), "end": float(e), "midi": int(current_pitch), "name": midi_to_name(int(current_pitch)), "velocity": int(round(vel))})
    return quantize_notes(notes, tempo=tempo, divisions=16)


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
    ap.add_argument("--input", help="Path to input audio (when transcribing from audio)")
    ap.add_argument("--input-json", help="Path to input notes JSON (when transposing only)")
    ap.add_argument("--instrument", default="auto")
    ap.add_argument("--output-json", required=True)
    ap.add_argument("--output-musicxml", required=True)
    ap.add_argument("--output-midi", help="Optional path to write MIDI file")
    ap.add_argument("--output-pdf", help="Optional path to write PDF via music21 backend")
    ap.add_argument("--tempo", type=int, default=0)
    ap.add_argument("--separate", choices=["auto", "demucs", "spleeter", "no"], default="no")
    args = ap.parse_args()

    # Transpose from existing notes JSON without audio reprocessing
    if args.input_json:
        data = json.loads(Path(args.input_json).read_text(encoding="utf-8"))
        notes = data.get("notes", [])
        meta = data.get("meta", {})
        tempo_est = int(meta.get("tempo", args.tempo or 120))
        target = args.instrument if args.instrument != "auto" else meta.get("instrument", "soprano")
        semitone_map = {"soprano": 2, "alto": 9, "tenor": 14, "baritone": 21}
        shift = semitone_map.get(target, 2)
        tnotes = []
        for n in notes:
            m = int(n["midi"]) + shift
            tnotes.append({**n, "midi": m, "name": midi_to_name(m)})
        s = to_stream(tnotes, instrument=target, tempo=tempo_est)
        meta["instrument"] = target
        Path(args.output_json).write_text(json.dumps({"meta": meta, "notes": tnotes}), encoding="utf-8")
        s.write("musicxml", fp=args.output_musicxml)
        if args.output_midi:
            s.write("midi", fp=args.output_midi)
        if args.output_pdf:
            s.write("musicxml.pdf", fp=args.output_pdf)
        return

    # Otherwise, transcribe from audio
    if not args.input:
        raise SystemExit("--input is required when not using --input-json")
    y, sr = librosa.load(args.input, sr=44100, mono=True)
    if np.max(np.abs(y)) > 0:
        y = y / np.max(np.abs(y))
    y = isolate_sax(y, sr, mode=args.separate)
    tempo_est = args.tempo
    if tempo_est <= 0:
        tempo_est, _ = librosa.beat.beat_track(y=y, sr=sr)
        if tempo_est <= 0:
            tempo_est = 120
    notes = transcribe(y, sr, tempo=tempo_est)

    if args.instrument == "auto":
        midis = np.array([n["midi"] for n in notes])
        med = float(np.median(midis)) if len(midis) else 69.0
        centers = {"soprano": 76, "alto": 69, "tenor": 62, "baritone": 55}
        instrument = min(centers.keys(), key=lambda k: abs(centers[k] - med))
    else:
        instrument = args.instrument

    s = to_stream(notes, instrument=instrument, tempo=tempo_est)
    try:
        key_obj = s.analyze('key')
        key_sig = key_obj.name
    except Exception:
        key_sig = "C major"
    meter = "4/4"

    Path(args.output_json).write_text(json.dumps({
        "meta": {"tempo": tempo_est, "key": key_sig, "meter": meter, "instrument": instrument},
        "notes": notes
    }), encoding="utf-8")
    s.write("musicxml", fp=args.output_musicxml)
    if args.output_midi:
        s.write("midi", fp=args.output_midi)
    if args.output_pdf:
        s.write("musicxml.pdf", fp=args.output_pdf)


if __name__ == "__main__":
    main()


