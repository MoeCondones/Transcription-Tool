#!/usr/bin/env python3
"""
Minimal CLI client to interact with the .NET API:
- Upload an audio file and instrument hint
- Poll status until done
- Download exports (stubbed for now)
"""
import argparse
import time
from pathlib import Path

import requests
from tqdm import tqdm


def upload(base_url: str, audio_path: Path, instrument_hint: str) -> str:
    with audio_path.open("rb") as f:
        files = {"audio": (audio_path.name, f, "application/octet-stream")}
        data = {"instrumentHint": instrument_hint}
        r = requests.post(f"{base_url}/transcriptions", files=files, data=data, timeout=60)
        r.raise_for_status()
        # Accepted returns location or body with id
        try:
            return r.json()["id"]
        except Exception:
            # fallback parse from location header (/transcriptions/{id})
            loc = r.headers.get("Location", "")
            return loc.rstrip("/").split("/")[-1]


def poll(base_url: str, tid: str) -> dict:
    while True:
        r = requests.get(f"{base_url}/transcriptions/{tid}", timeout=30)
        if r.status_code == 404:
            time.sleep(0.5)
            continue
        r.raise_for_status()
        data = r.json()
        status = data.get("status", "queued")
        tqdm.write(f"status: {status}")
        if status in {"done", "error"}:
            return data
        time.sleep(1.0)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("audio", type=Path, help="Path to audio file")
    ap.add_argument("--base-url", default="http://localhost:5000")
    ap.add_argument("--hint", default="auto", choices=["auto", "alto", "tenor", "baritone", "soprano"])
    args = ap.parse_args()

    tid = upload(args.base_url, args.audio, args.hint)
    tqdm.write(f"uploaded id: {tid}")
    info = poll(args.base_url, tid)
    tqdm.write(f"final: {info}")


if __name__ == "__main__":
    main()


