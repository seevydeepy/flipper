#!/usr/bin/env python3
"""Batch score audit via the authoritative Flipper.Core pipeline.

Flipper.Core (ScoreFactInference + flipper-score CLI) is the single
implementation that decides score identity. This script prepares reference
data and drives batch runs: it never maintains a competing inference
algorithm. Legacy heuristics are kept only as a text-extraction fallback
for environments where dotnet is unavailable.

Usage:
  python scripts/audit_scores.py --root <library> [--out <catalog.json>]
      [--dotnet <flipper-score.dll>] [--dry-run] [--apply-to <catalog>]
      [--backup <backup-path>] [--limit N]
  python scripts/audit_scores.py --self-check   # extraction-parity tests

--dry-run (default): print the summary, write --out only.
--apply-to: merge proposals into an existing catalog with the same
  concurrency/merge contract as the app (stale proposals are skipped,
  failures leave the original catalog intact).
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

DEFAULT_ROOT = Path(r"//Alexandria/Charles/Scores")
REPO = Path(__file__).resolve().parent.parent
DEFAULT_DLL = (
    REPO / "src" / "Flipper.ScoreCli" / "bin" / "Release" / "net8.0" / "flipper-score.dll"
)

JUNK = re.compile(
    r"public domain|creative commons|mutopia|typeset|licensed under|reference:|"
    r"free to download|creativecommons|copyright|this sheet music|"
    r"sheet music from www|unsaved publication|www\.|https?://|finale \d|"
    r"untitled\d*|created o[nm]",
    re.I,
)
MEASURE = re.compile(r"^\d{1,3}[a-z]?$")


def clean(text: str) -> str:
    text = "".join(ch if ch >= " " else " " for ch in text)
    text = text.replace("•", " ").replace("©", " ")
    text = text.replace("（", "(").replace("）", ")")
    text = re.sub(r"\s+", " ", text).strip(" \u00a0-–_|")
    return text


def useful(line: str) -> bool:
    """Extraction fallback only: keep plausible text lines.

    Identity decisions (titles, composers, credits) live in Flipper.Core;
    this only decides which raw lines are worth forwarding.
    """
    if not line or MEASURE.match(line) or JUNK.search(line):
        return False
    letters = sum(ch.isalpha() for ch in line)
    if letters < 3 or letters / max(len(line), 1) < 0.35:
        return False
    symbols = sum(not ch.isalnum() and not ch.isspace() for ch in line)
    return symbols <= letters


def folder_composer(rel: Path) -> str:
    parts = rel.parts
    if len(parts) >= 2 and parts[0].lower() == "corpus":
        return parts[1]
    return ""


def page_text(pdf: Path) -> tuple[dict, list[str]]:
    """Extract (metadata, lines) with pymupdf; OCR stays in the app/CLI."""
    try:
        import pymupdf
    except ImportError:
        return {}, []
    try:
        doc = pymupdf.open(pdf)
    except Exception:
        return {}, []
    try:
        if not doc.page_count:
            return {}, []
        meta = doc.metadata or {}
        # First page plus up to two more when the first is inconclusive.
        texts = []
        for index in range(min(3, doc.page_count)):
            texts.append(doc[index].get_text("text"))
            letters = sum(ch.isalpha() for ch in texts[-1])
            if letters >= 60:
                break
        return meta, texts
    finally:
        doc.close()


def lines_from_text(texts: list[str]) -> list[str]:
    found: list[str] = []
    for text in texts:
        for raw in text.splitlines():
            line = clean(raw)
            if useful(line) and line not in found:
                found.append(line)
            if len(found) >= 30:
                return found
    return found


def infer_via_cli(dll: Path, payload: dict) -> dict | None:
    """Ask the authoritative Core pipeline; None when dotnet is unavailable."""
    if not dll.exists():
        return None
    with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False, encoding="utf-8") as tmp:
        json.dump(payload, tmp)
        tmp_path = tmp.name
    try:
        proc = subprocess.run(
            ["dotnet", str(dll), "infer-text", tmp_path, "--json"],
            capture_output=True, text=True, cwd=REPO)
    finally:
        Path(tmp_path).unlink(missing_ok=True)
    if proc.returncode != 0:
        raise RuntimeError(f"flipper-score failed: {proc.stderr.strip()[-300:]}")
    return json.loads(proc.stdout)


def extract(pdf: Path, rel: Path, dll: Path) -> dict[str, str]:
    meta, texts = page_text(pdf)
    page_lines = lines_from_text(texts)
    file_title = pdf.stem
    folder = folder_composer(rel)

    decided = infer_via_cli(dll, {
        "fileName": pdf.name,
        "metadata": {
            "title": meta.get("title"),
            "author": meta.get("author"),
            "subject": meta.get("subject"),
        },
        "lines": page_lines,
    })
    if decided is not None:
        facts = {
            "title": (decided.get("title") or file_title)[:160],
            "subtitle": (decided.get("subtitle") or "")[:160],
            "composer": (decided.get("composer") or "")[:80],
        }
        if not facts["composer"]:
            facts["composer"] = folder
        return facts

    # Fallback (no dotnet): forward the filename; composer unknown unless the
    # folder corroborates. Never invent attribution here.
    return {"title": file_title[:160], "subtitle": "", "composer": folder[:80]}


def write_catalog(path: Path, catalog: dict[str, dict[str, str]]) -> None:
    payload = json.dumps(catalog, indent=2, ensure_ascii=False)
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(path.name + ".tmp")
    tmp.write_text(payload, encoding="utf-8")
    os.replace(tmp, path)


def apply_to_catalog(catalog_path: Path, proposals: dict, backup: Path | None) -> dict:
    """Merge proposals with the app's contract: stale entries are skipped and
    a failed write leaves the original catalog intact."""
    current = json.loads(catalog_path.read_text(encoding="utf-8"))
    if backup is not None:
        write_catalog(backup, current)
    applied: list[str] = []
    skipped: list[dict] = []
    for key, facts in proposals.items():
        entry = next((k for k in current if k.lower() == key.lower()), None)
        if entry is not None:
            skipped.append({"path": key, "reason": "exists-preserved"})
            continue
        current[key] = facts
        applied.append(key)
    before = catalog_path.read_text(encoding="utf-8")
    try:
        write_catalog(catalog_path, current)
        read_back = json.loads(catalog_path.read_text(encoding="utf-8"))
    except Exception as exc:
        catalog_path.write_text(before, encoding="utf-8")
        return {"applied": [], "skipped": [{"path": k, "reason": f"write-failed: {exc}"} for k in proposals]}
    confirmed = sum(1 for k in applied if (read_back.get(k) or {}) == proposals[k])
    return {"applied": applied, "skipped": skipped, "confirmed": confirmed}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=str(DEFAULT_ROOT))
    parser.add_argument("--out", default="")
    parser.add_argument("--dotnet", default=str(DEFAULT_DLL))
    parser.add_argument("--limit", type=int, default=0)
    parser.add_argument("--dry-run", action="store_true", default=True)
    parser.add_argument("--apply-to", default="")
    parser.add_argument("--backup", default="")
    parser.add_argument("--self-check", action="store_true")
    args = parser.parse_args()

    if args.self_check:
        return self_check()

    root = Path(args.root)
    dll = Path(args.dotnet)
    catalog: dict[str, dict[str, str]] = {}
    errors = 0
    pdfs = sorted(root.rglob("*.pdf"))
    if args.limit:
        pdfs = pdfs[: args.limit]
    for pdf in pdfs:
        rel = pdf.relative_to(root)
        try:
            catalog[str(rel).replace("/", "\\")] = extract(pdf, rel, dll)
        except Exception:
            errors += 1
            catalog[str(rel).replace("/", "\\")] = {
                "title": pdf.stem, "subtitle": "", "composer": folder_composer(rel)}

    if args.out:
        write_catalog(Path(args.out), catalog)

    result: dict = {
        "files": len(catalog),
        "with_composer": sum(1 for item in catalog.values() if item.get("composer")),
        "errors": errors,
        "engine": "core-cli" if dll.exists() else "filename-fallback",
        "path": args.out or "(dry-run, no writes)",
        "dry_run": not args.apply_to,
    }
    if args.apply_to:
        catalog_path = Path(args.apply_to)
        backup = Path(args.backup) if args.backup else None
        merge = apply_to_catalog(catalog_path, catalog, backup)
        result["merge"] = merge
    print(json.dumps(result, indent=2))
    return 0


def self_check() -> int:
    cases = [
        ("John Williams", True),
        ("(Main Theme)", True),
        ("789: ;<=> 9: ?9@AB", False),
        ("Copyright 2026", False),
    ]
    failed = 0
    for line, want in cases:
        got = useful(line)
        if got != want:
            print(f"FAIL useful({line!r}) = {got}, want {want}")
            failed += 1
    print("self-check: " + ("ok" if not failed else f"{failed} failures"))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
