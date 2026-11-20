#!/usr/bin/env python3
"""
Server-side watcher template for Alterius SMB share output.

Monitors the SMB share root for finalized chunks (identified by manifest.json),
strictly ignores incomplete '.uploading' directories, verifies SHA-256 checksums
and file sizes, parses telemetry events.jsonl, and exposes clean hooks for
Whisper speech transcription + LLM activity analysis.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import logging
import os
import sqlite3
import sys
import time
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S",
)
logger = logging.getLogger("server_watcher")


def init_state_db(db_path: Path) -> sqlite3.Connection:
    db_path.parent.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(str(db_path))
    conn.execute(
        """
        CREATE TABLE IF NOT EXISTS processed_chunks (
            chunk_key TEXT PRIMARY KEY,
            manifest_path TEXT NOT NULL,
            machine_name TEXT,
            windows_user TEXT,
            session_id TEXT,
            chunk_name TEXT,
            duration_sec REAL,
            status TEXT NOT NULL,
            processed_at_utc TEXT NOT NULL,
            output_path TEXT
        )
        """
    )
    conn.commit()
    return conn


def is_chunk_processed(conn: sqlite3.Connection, chunk_key: str) -> bool:
    row = conn.execute(
        "SELECT status FROM processed_chunks WHERE chunk_key = ?",
        (chunk_key,),
    ).fetchone()
    return row is not None and row[0] == "done"


def mark_chunk_status(
    conn: sqlite3.Connection,
    chunk_key: str,
    manifest_path: Path,
    manifest: dict[str, Any],
    status: str,
    output_path: Path | None,
) -> None:
    conn.execute(
        """
        INSERT INTO processed_chunks (
            chunk_key, manifest_path, machine_name, windows_user,
            session_id, chunk_name, duration_sec, status, processed_at_utc, output_path
        )
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
        ON CONFLICT(chunk_key) DO UPDATE SET
            status = excluded.status,
            processed_at_utc = excluded.processed_at_utc,
            output_path = excluded.output_path
        """,
        (
            chunk_key,
            str(manifest_path),
            manifest.get("machine_name", ""),
            manifest.get("windows_user", ""),
            manifest.get("session_id", ""),
            manifest.get("chunk_name", ""),
            float(manifest.get("duration_sec", 0.0)),
            status,
            datetime.now(timezone.utc).isoformat(),
            str(output_path) if output_path else None,
        ),
    )
    conn.commit()


def compute_sha256(file_path: Path) -> str:
    digest = hashlib.sha256()
    with file_path.open("rb") as fh:
        while chunk := fh.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest().lower()


def verify_chunk_integrity(chunk_dir: Path, manifest: dict[str, Any]) -> tuple[bool, str]:
    files = manifest.get("files", [])
    if not isinstance(files, list):
        return False, "manifest 'files' field is not a list"

    for entry in files:
        name = entry.get("name")
        expected_size = entry.get("size_bytes")
        expected_sha = (entry.get("sha256") or "").lower()

        if not name:
            return False, "manifest contains file entry without 'name'"

        file_path = chunk_dir / name
        if not file_path.is_file():
            return False, f"missing file: {name}"

        actual_size = file_path.stat().st_size
        if expected_size is not None and actual_size != expected_size:
            return False, f"size mismatch for {name}: expected={expected_size}, actual={actual_size}"

        if expected_sha:
            actual_sha = compute_sha256(file_path)
            if actual_sha != expected_sha:
                return False, f"sha256 mismatch for {name}: expected={expected_sha}, actual={actual_sha}"

    return True, "ok"


def summarize_telemetry(events_path: Path) -> dict[str, Any]:
    event_counts: Counter[str] = Counter()
    click_buttons: Counter[str] = Counter()
    total_key_bursts = 0
    focus_timeline: list[dict[str, Any]] = []

    if not events_path.is_file():
        return {
            "event_counts": {},
            "click_buttons": {},
            "total_typing_key_count": 0,
            "focus_timeline": [],
        }

    with events_path.open("r", encoding="utf-8-sig") as fh:
        for line_no, raw_line in enumerate(fh, start=1):
            stripped = raw_line.strip()
            if not stripped:
                continue

            try:
                evt = json.loads(stripped)
            except json.JSONDecodeError as exc:
                logger.warning("Skipping malformed line %d in %s: %s", line_no, events_path, exc)
                continue

            evt_type = str(evt.get("type", "unknown"))
            event_counts[evt_type] += 1

            if evt_type == "mouse_click":
                click_buttons[str(evt.get("button", "unknown"))] += 1
            elif evt_type == "typing_burst":
                total_key_bursts += int(evt.get("key_count", 0) or 0)
            elif evt_type == "window_focus":
                focus_timeline.append(
                    {
                        "offset_ms": evt.get("offset_ms", 0),
                        "process_name": evt.get("process_name", ""),
                        "window_title": evt.get("window_title", ""),
                    }
                )

    return {
        "event_counts": dict(event_counts),
        "click_buttons": dict(click_buttons),
        "total_typing_key_count": total_key_bursts,
        "focus_timeline": focus_timeline[:100],
    }


def transcribe_audio_with_whisper(chunk_dir: Path, manifest: dict[str, Any]) -> dict[str, Any]:
    """
    Placeholder integration hook for Whisper speech-to-text.

    Replace the body with your Whisper client (e.g., faster-whisper or OpenAI Whisper API)
    to transcribe both channels independently:
      - audio_mic.ogg -> employee speech ('mic')
      - audio_sys.ogg -> remote call participants / system audio ('system')
    """
    transcripts: dict[str, Any] = {}
    for channel, filename in (("mic", "audio_mic.ogg"), ("system", "audio_sys.ogg")):
        audio_path = chunk_dir / filename
        if audio_path.is_file():
            # Example with faster-whisper:
            # segments, info = whisper_model.transcribe(str(audio_path), language="ru")
            # text = " ".join(seg.text.strip() for seg in segments)
            transcripts[channel] = {
                "file": filename,
                "size_bytes": audio_path.stat().st_size,
                "status": "ready_for_whisper",
                "text": "",
            }
    return transcripts


def analyze_chunk_with_llm(
    manifest: dict[str, Any],
    telemetry_summary: dict[str, Any],
    transcripts: dict[str, Any],
) -> dict[str, Any]:
    """
    Placeholder integration hook for LLM post-processing (e.g., task classification,
    call summarization, or productivity insights from manifest + telemetry + transcripts).
    """
    prompt_payload = {
        "user": f"{manifest.get('machine_name')}\\{manifest.get('windows_user')}",
        "started_at_utc": manifest.get("started_at_utc"),
        "duration_sec": manifest.get("duration_sec"),
        "top_apps": manifest.get("top_apps", []),
        "telemetry": telemetry_summary,
        "audio_channels": transcripts,
    }

    # Example:
    # response = openai_client.responses.create(
    #     model="gpt-4.1-mini",
    #     input=f"Summarize employee activity and calls from JSON:\n{json.dumps(prompt_payload, ensure_ascii=False)}"
    # )
    return {
        "status": "ready_for_llm",
        "prompt_payload": prompt_payload,
    }


def discover_ready_manifests(root_dir: Path) -> list[Path]:
    discovered: list[Path] = []
    if not root_dir.exists():
        return discovered

    for current_root, dirnames, filenames in os.walk(root_dir):
        # Strictly ignore any directory still being staged atomically by the agent.
        dirnames[:] = sorted(d for d in dirnames if not d.endswith(".uploading"))

        if "manifest.json" in filenames:
            discovered.append(Path(current_root) / "manifest.json")

    return discovered


def process_once(root_dir: Path, conn: sqlite3.Connection, output_dir: Path) -> int:
    manifests = discover_ready_manifests(root_dir)
    processed_now = 0

    for manifest_path in manifests:
        chunk_dir = manifest_path.parent
        chunk_key = str(chunk_dir.relative_to(root_dir)).replace("\\", "/")

        if is_chunk_processed(conn, chunk_key):
            continue

        try:
            manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
        except Exception as exc:
            logger.error("Failed reading %s: %s", manifest_path, exc)
            continue

        ok, reason = verify_chunk_integrity(chunk_dir, manifest)
        if not ok:
            logger.warning("Chunk %s failed verification (%s); will retry on next scan.", chunk_key, reason)
            continue

        logger.info(
            "Processing chunk %s (user=%s\\%s, duration=%.1fs)",
            chunk_key,
            manifest.get("machine_name", "?"),
            manifest.get("windows_user", "?"),
            float(manifest.get("duration_sec", 0.0)),
        )

        telemetry_summary = summarize_telemetry(chunk_dir / "events.jsonl")
        transcripts = transcribe_audio_with_whisper(chunk_dir, manifest)
        llm_package = analyze_chunk_with_llm(manifest, telemetry_summary, transcripts)

        output_dir.mkdir(parents=True, exist_ok=True)
        safe_name = chunk_key.replace("/", "__") + ".ai_package.json"
        out_file = output_dir / safe_name
        out_file.write_text(
            json.dumps(llm_package, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )

        mark_chunk_status(conn, chunk_key, manifest_path, manifest, "done", out_file)
        processed_now += 1
        logger.info("Chunk %s processed -> %s", chunk_key, out_file)

    return processed_now


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Watch Alterius SMB share for completed chunks (manifest.json)."
    )
    parser.add_argument(
        "--root",
        required=True,
        help="Root directory of the SMB share (e.g. /mnt/alterius_share or \\\\SERVER\\AlteriusShare).",
    )
    parser.add_argument(
        "--state-db",
        default="./processed_chunks.sqlite",
        help="Path to SQLite tracking database (default: ./processed_chunks.sqlite).",
    )
    parser.add_argument(
        "--output-dir",
        default="./ai_analysis_out",
        help="Directory to write AI-ready chunk packages (default: ./ai_analysis_out).",
    )
    parser.add_argument(
        "--interval",
        type=int,
        default=10,
        help="Polling interval in seconds when running continuously (default: 10).",
    )
    parser.add_argument(
        "--once",
        action="store_true",
        help="Run one scan pass and exit.",
    )
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    root_dir = Path(args.root).resolve()
    state_db = Path(args.state_db).resolve()
    output_dir = Path(args.output_dir).resolve()

    logger.info("Watching root=%s | state_db=%s | output_dir=%s", root_dir, state_db, output_dir)
    conn = init_state_db(state_db)

    try:
        while True:
            count = process_once(root_dir, conn, output_dir)
            if count > 0:
                logger.info("Pass complete: processed %d new chunk(s).", count)

            if args.once:
                break

            time.sleep(max(1, args.interval))
    except KeyboardInterrupt:
        logger.info("Watcher stopped by user.")
    finally:
        conn.close()

    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
