#!/usr/bin/env python3
"""Read PDFs under Scores and write .flipper-catalog.json with title and composer."""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

import pymupdf

ROOT = Path(r"//Alexandria/Charles/Scores")
CATALOG = ROOT / ".flipper-catalog.json"

JUNK = re.compile(
    r"public domain|creative commons|mutopia|typeset|licensed under|reference:|"
    r"free to download|creativecommons|copyright\s*©|this sheet music|"
    r"sheet music from www|unsaved publication|www\.|https?://|copyright",
    re.I,
)
MEASURE = re.compile(r"^\d{1,3}[a-z]?$")
META_JUNK = re.compile(r"^(title|untitled|document|lg-\d+|microsoft word)", re.I)
BAD_COMPOSER = re.compile(
    r"^(pedal|piano|basso|violino|viola|cello|flute|guitar|soprano|alto|tenor|"
    r"bass|tema|andantino|allegro|andante|adagio|hob\.|op\.|bwv|brido|arr\.|"
    r"sheet music)",
    re.I,
)
BYLINE = re.compile(r"^(?:by|arr\.?|arranged by|transc(?:ribed)?\.? by)\s+(.+)$", re.I)
YEARS = re.compile(r"\s*\(\s*\d{3,4}(?:\s*[-–]\s*\d{2,4})?\s*\)\s*")


def clean(text: str) -> str:
    text = "".join(ch if ch >= " " else " " for ch in text)
    text = text.replace("•", " ").replace("©", " ")
    text = re.sub(r"\s+", " ", text).strip(" \u00a0-–_|")
    return text


def useful(line: str) -> bool:
    if not line or MEASURE.match(line) or JUNK.search(line):
        return False
    letters = sum(ch.isalpha() for ch in line)
    return letters >= 3 and letters / max(len(line), 1) >= 0.45


def meta_ok(value: str | None) -> str:
    value = clean(value or "")
    if not value or META_JUNK.search(value):
        return ""
    return value


def lines_from(pdf: Path) -> list[str]:
    try:
        doc = pymupdf.open(pdf)
    except Exception:
        return []
    try:
        text = ""
        if doc.page_count:
            text = doc[0].get_text("text")
        found = []
        for raw in text.splitlines():
            line = clean(raw)
            if useful(line):
                found.append(line)
            if len(found) >= 8:
                break
        return found
    finally:
        doc.close()


def split_dash(value: str) -> tuple[str, str]:
    parts = [clean(part) for part in re.split(r"\s+[-–]\s+", value) if clean(part)]
    if len(parts) >= 2 and 2 <= len(parts[0].split()) <= 5 and looks_like_name(parts[0]):
        return parts[1], parts[0]
    if len(parts) >= 2 and looks_like_name(parts[-1]):
        return " - ".join(parts[:-1]), parts[-1]
    return value, ""


def looks_like_name(value: str) -> bool:
    words = [word for word in YEARS.sub("", value).split() if word]
    if not 2 <= len(words) <= 6:
        return False
    if any(word.lower() in {"piano", "solo", "arr", "opus", "op."} for word in words):
        return False
    caps = sum(word[:1].isupper() for word in words if word[:1].isalpha())
    return caps >= max(1, len(words) - 1)


def folder_composer(rel: Path) -> str:
    parts = rel.parts
    if len(parts) >= 2 and parts[0].lower() == "corpus":
        return parts[1]
    return ""


def extract(pdf: Path, rel: Path) -> dict[str, str]:
    try:
        doc = pymupdf.open(pdf)
        meta = doc.metadata or {}
        doc.close()
    except Exception:
        meta = {}

    title = meta_ok(meta.get("title"))
    composer = meta_ok(meta.get("author"))
    if title and not composer:
        title, composer = split_dash(title)
    page_lines = lines_from(pdf)
    folder = folder_composer(rel)

    if page_lines:
        first = page_lines[0]
        byline = BYLINE.match(first)
        if byline or looks_like_name(first) or (folder and folder.lower() in first.lower()):
            composer = composer or YEARS.sub("", byline.group(1) if byline else first).strip()
            for line in page_lines[1:]:
                if looks_like_name(line) or (folder and folder.lower() in line.lower()) or BAD_COMPOSER.search(line):
                    continue
                title = title or line.strip('"')
                break
        elif not title:
            title = first
    if title:
        maybe_title, maybe_composer = split_dash(title)
        if maybe_composer and not composer:
            title, composer = maybe_title, maybe_composer

    if not composer:
        for line in page_lines[1:]:
            match = BYLINE.match(line)
            if match:
                composer = clean(match.group(1))
                break
            if looks_like_name(line) and line.lower() != (title or "").lower():
                composer = YEARS.sub("", line).strip()
                break

    if not composer:
        by_file = re.search(r"\bby\s+(.+)$", pdf.stem.replace("-", " "), flags=re.I)
        if by_file:
            composer = clean(by_file.group(1))
    if not composer:
        composer = folder

    if composer and (BAD_COMPOSER.search(composer) or composer.lower() == (title or "").lower()):
        composer = folder_composer(rel)

    if not title or JUNK.search(title) or len(title) < 3:
        title = pdf.stem
    if composer and JUNK.search(composer):
        composer = folder_composer(rel)

    title = YEARS.sub("", title).strip(" \"'-")
    composer = YEARS.sub("", composer).strip(" \"'-")
    if composer.lower() == (title or "").lower():
        composer = folder_composer(rel)

    return {"title": title[:160], "composer": composer[:80]}


def main() -> int:
    catalog = {}
    errors = 0
    for pdf in sorted(ROOT.rglob("*.pdf")):
        rel = pdf.relative_to(ROOT)
        try:
            catalog[str(rel).replace("/", "\\")] = extract(pdf, rel)
        except Exception:
            errors += 1
            catalog[str(rel).replace("/", "\\")] = {"title": pdf.stem, "composer": folder_composer(rel)}

    CATALOG.write_text(json.dumps(catalog, indent=2, ensure_ascii=False), encoding="utf-8")
    named = sum(1 for item in catalog.values() if item.get("composer"))
    print(json.dumps({"files": len(catalog), "with_composer": named, "errors": errors, "path": str(CATALOG)}, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
