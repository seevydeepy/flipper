#!/usr/bin/env python3
"""Synthetic score-identification fixtures + before/after evaluation harness.

Reads fixtures from tests/eval/fixtures/*.json, runs each through the
authoritative Core path (flipper-score infer-text), and reports
title/composer accuracy plus the required failure-class breakdown.

Fixture schema:
  {"fileName": "x.pdf", "metadata": {"title":..,"author":..,"subject":..},
   "lines": [...], "expected": {"title":..,"composer":..,"subtitle":..},
   "case": "<area>", "notes": "..."}
Expected null means "must abstain" (unknown is better than wrong).
Composer expected starting with "!" means "must NOT equal this value".

Usage:
  python tests/eval/run_eval.py --cli src/Flipper.ScoreCli/bin/Release/net8.0/flipper-score.dll [--before results/before.json] [--out results/after.json]
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
FIXTURES = Path(__file__).resolve().parent / "fixtures"


def run_cli(dll: str, fixture: Path) -> dict:
    proc = subprocess.run(
        ["dotnet", dll, "infer-text", str(fixture), "--json"],
        capture_output=True, text=True, cwd=REPO)
    if proc.returncode != 0:
        raise RuntimeError(f"cli failed on {fixture.name}: {proc.stderr.strip()}")
    return json.loads(proc.stdout)


def norm(value: str | None) -> str:
    return (value or "").strip().lower()


def check_title(got: str | None, want: str | None) -> str:
    if want is None:
        return "unresolved" if not (got or "").strip() else "wrong"
    if want == "*":
        return "ok" if (got or "").strip() else "unresolved"
    return "ok" if norm(got) == norm(want) else "wrong"


def check_composer(got: str | None, want: str | None) -> str:
    if want is None:
        return "unresolved" if not (got or "").strip() else "wrong"
    if want.startswith("!"):
        return "ok" if norm(got) != norm(want[1:]) and norm(got) != "" or norm(got) == "" and False else ("ok" if norm(got) != norm(want[1:]) else "wrong-attribution")
    return "ok" if norm(got) == norm(want) else ("wrong-attribution" if (got or "").strip() else "unresolved")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--cli", required=True)
    parser.add_argument("--out", default="")
    args = parser.parse_args()

    fixtures = sorted(FIXTURES.glob("*.json"))
    if not fixtures:
        print("no fixtures", file=sys.stderr)
        return 2

    rows = []
    for fixture in fixtures:
        spec = json.loads(fixture.read_text(encoding="utf-8"))
        with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False, encoding="utf-8") as tmp:
            json.dump({k: spec[k] for k in ("fileName", "metadata", "lines") if k in spec}, tmp)
            tmp_path = tmp.name
        try:
            got = run_cli(args.cli, Path(tmp_path))
        finally:
            Path(tmp_path).unlink(missing_ok=True)
        want = spec.get("expected", {})
        rows.append({
            "fixture": fixture.name,
            "case": spec.get("case", ""),
            "title": check_title(got.get("title"), want.get("title")),
            "composer": check_composer(got.get("composer"), want.get("composer")),
            "got": got,
            "want": want,
        })

    def rate(key: str, ok: str = "ok") -> float:
        return sum(1 for r in rows if r[key] == ok) / len(rows)

    title_ok = sum(1 for r in rows if r["title"] == "ok")
    comp_ok = sum(1 for r in rows if r["composer"] in ("ok",))
    comp_wrong = sum(1 for r in rows if r["composer"] in ("wrong", "wrong-attribution"))
    comp_unresolved = sum(1 for r in rows if r["composer"] == "unresolved")
    print(json.dumps({
        "fixtures": len(rows),
        "title_accuracy": round(title_ok / len(rows), 3),
        "composer_accuracy": round(comp_ok / len(rows), 3),
        "composer_wrong_attribution": comp_wrong,
        "composer_unresolved": comp_unresolved,
        "by_case": sorted({r["case"] for r in rows}),
        "rows": rows,
    }, indent=2, ensure_ascii=False))

    if args.out:
        Path(args.out).parent.mkdir(parents=True, exist_ok=True)
        Path(args.out).write_text(json.dumps({
            "fixtures": len(rows),
            "title_accuracy": round(title_ok / len(rows), 3),
            "composer_accuracy": round(comp_ok / len(rows), 3),
            "composer_wrong_attribution": comp_wrong,
            "composer_unresolved": comp_unresolved,
            "rows": rows,
        }, indent=2, ensure_ascii=False), encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
