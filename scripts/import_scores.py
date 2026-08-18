#!/usr/bin/env python3
"""Copy Enscore PDFs into Scores/Downloads with cleaned names, then fetch a Mutopia corpus."""

from __future__ import annotations

import hashlib
import html
import os
import re
import time
import urllib.error
import urllib.request
from collections import defaultdict
from pathlib import Path

SRC = Path(r"//Alexandria/Charles/Enscore")
DST_ROOT = Path(r"//Alexandria/Charles/Scores")
DOWNLOADS = DST_ROOT / "Downloads"
CORPUS = DST_ROOT / "Corpus"
MUTOPIA_FTP = "https://www.mutopiaproject.org/ftp/"

SMALL = {
    "a", "an", "and", "as", "at", "by", "de", "del", "des", "di", "du",
    "for", "from", "in", "la", "le", "of", "on", "or", "the", "to", "van", "von", "with",
}

SITE_JUNK = re.compile(
    r"(sheetmusic[- ]?free\.com_?|sheetsdaily|thepianonotes\.com_?|"
    r"musicalibra|oktav|pdfcoffee\.com|kupdf\.net|free-scores\.com|"
    r"musescore\.com|8notes)",
    re.I,
)
TRAILING_COPY = re.compile(r"\s*[\(\[]\s*\d+\s*[\)\]]\s*$")
VERSION_TAIL = re.compile(r"\s*(?:·\s*)?version\s*\d+\s*$", re.I)
PREFIX_JUNK = re.compile(r"^\[(?:free-scores\.com|oktav|musicalibra)\]_?\s*", re.I)
HEX_TAIL = re.compile(r"[-_ ][a-f0-9]{8,}$", re.I)
DATE_PREFIX = re.compile(r"^\d{4}[-_. ]+\d{2}[-_. ]+\d{2}\s*")
LEADING_INDEX = re.compile(r"^\d{1,3}\s+-\s+")
OPUS = re.compile(r"\b(?:op(?:us)?\.?\s*)(\d+)\s*(?:no\.?\s*)?(\d+)?", re.I)


def clean_stem(name: str) -> str:
    stem = Path(name).stem
    stem = PREFIX_JUNK.sub("", stem)
    stem = DATE_PREFIX.sub("", stem)
    stem = SITE_JUNK.sub(" ", stem)
    stem = VERSION_TAIL.sub("", stem)
    stem = TRAILING_COPY.sub("", stem)
    stem = stem.replace("&amp;", "and")
    stem = stem.replace("_", " ")
    stem = re.sub(r"(?<=\w)-(?=\w)", " ", stem)
    stem = re.sub(r"\s*-\s*", " - ", stem)
    stem = re.sub(r"\s+", " ", stem).strip(" .-_")
    stem = HEX_TAIL.sub("", stem).strip(" -")
    stem = LEADING_INDEX.sub("", stem).strip(" -")
    if not stem:
        return "Untitled"
    words = []
    for i, raw in enumerate(stem.split(" ")):
        word = raw.strip()
        if not word:
            continue
        lower = word.lower()
        if lower in SMALL and i not in (0, len(stem.split(" ")) - 1) and not word.isupper():
            words.append(lower)
        elif re.fullmatch(r"[A-Za-z]\.[A-Za-z]\.", word):
            words.append(word.upper())
        elif word.isupper() and len(word) > 3:
            words.append(word.title())
        else:
            words.append(word[:1].upper() + word[1:] if word else word)
    title = " ".join(words)
    title = re.sub(r"\s+-\s+", " - ", title)
    title = re.sub(r"\s{2,}", " ", title).strip(" -")
    return title[:140] or "Untitled"


def safe_name(title: str) -> str:
    cleaned = re.sub(r'[<>:"/\\|?*]', "", title).strip(" .")
    return (cleaned or "Untitled") + ".pdf"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def copy_unique(src: Path, dest_dir: Path, title: str, seen_hash: dict[str, Path], used_names: set[str]) -> Path | None:
    if src.stat().st_size == 0:
        return None
    digest = sha256(src)
    if digest in seen_hash:
        return None
    dest_dir.mkdir(parents=True, exist_ok=True)
    name = safe_name(title)
    stem = Path(name).stem
    candidate = dest_dir / name
    n = 2
    while candidate.name.lower() in used_names:
        candidate = dest_dir / f"{stem} ({n}).pdf"
        n += 1
    used_names.add(candidate.name.lower())
    data = src.read_bytes()
    candidate.write_bytes(data)
    seen_hash[digest] = candidate
    return candidate


def import_enscore() -> dict:
    seen: dict[str, Path] = {}
    used: set[str] = set()
    copied = 0
    skipped_empty = 0
    skipped_dup = 0
    for path in SRC.rglob("*"):
        if not path.is_file():
            continue
        if path.suffix.lower() != ".pdf":
            continue
        if path.stat().st_size == 0:
            skipped_empty += 1
            continue
        dest = copy_unique(path, DOWNLOADS, clean_stem(path.name), seen, used)
        if dest is None:
            skipped_dup += 1
        else:
            copied += 1
    return {"copied": copied, "dupes": skipped_dup, "empty": skipped_empty, "unique": len(seen)}


def composer_folder(code: str) -> str:
    mapping = {
        "BachJS": "Bach",
        "BeethovenLv": "Beethoven",
        "BrahmsJ": "Brahms",
        "ChopinF": "Chopin",
        "DebussyC": "Debussy",
        "DvorakA": "Dvorak",
        "FaureG": "Faure",
        "GriegE": "Grieg",
        "HandelGF": "Handel",
        "HaydnFJ": "Haydn",
        "JoplinS": "Joplin",
        "LisztF": "Liszt",
        "MendelssohnF": "Mendelssohn",
        "MozartWA": "Mozart",
        "RachmaninoffS": "Rachmaninoff",
        "RavelM": "Ravel",
        "SatieE": "Satie",
        "SchubertF": "Schubert",
        "SchumannR": "Schumann",
        "ScriabinA": "Scriabin",
        "TchaikovskyPI": "Tchaikovsky",
        "VivaldiA": "Vivaldi",
    }
    return mapping.get(code, code)


def list_mutopia_pdfs(url: str, depth: int = 0) -> list[tuple[str, str]]:
    if depth > 6:
        return []
    try:
        with urllib.request.urlopen(url, timeout=30) as response:
            page = response.read().decode("utf-8", "ignore")
    except urllib.error.URLError:
        return []
    found: list[tuple[str, str]] = []
    for href in re.findall(r'href="([^"]+)"', page, flags=re.I):
        href = html.unescape(href)
        if href.startswith("?") or href.startswith("/") or href.startswith("http"):
            continue
        if href in ("../", "./"):
            continue
        if href.lower().endswith("-let.pdf"):
            found.append((url + href, href))
        elif href.endswith("/"):
            found.extend(list_mutopia_pdfs(url + href, depth + 1))
    return found


def download_corpus(limit: int | None = None) -> dict:
    # Walk composer roots only, not the whole tree blindly if listing is huge.
    try:
        with urllib.request.urlopen(MUTOPIA_FTP, timeout=30) as response:
            index = response.read().decode("utf-8", "ignore")
    except urllib.error.URLError as exc:
        return {"error": str(exc), "copied": 0}

    composers = [
        href.rstrip("/")
        for href in re.findall(r'href="([^"]+/)"', index, flags=re.I)
        if href not in ("../", "./") and not href.startswith("?")
    ]
    seen: dict[str, Path] = {}
    used_by_folder: dict[str, set[str]] = defaultdict(set)
    copied = 0
    failed = 0
    for composer in composers:
        folder = composer_folder(composer)
        dest_dir = CORPUS / folder
        pdfs = list_mutopia_pdfs(MUTOPIA_FTP + composer + "/")
        for url, filename in pdfs:
            if limit is not None and copied >= limit:
                return {"copied": copied, "failed": failed}
            title = clean_stem(filename.replace("-let", ""))
            dest_name = safe_name(title)
            dest = dest_dir / dest_name
            used = used_by_folder[folder]
            n = 2
            while dest.name.lower() in used:
                dest = dest_dir / f"{Path(dest_name).stem} ({n}).pdf"
                n += 1
            dest_dir.mkdir(parents=True, exist_ok=True)
            try:
                req = urllib.request.Request(url, headers={"User-Agent": "FlipperCorpus/1.0"})
                with urllib.request.urlopen(req, timeout=60) as response:
                    data = response.read()
                if not data.startswith(b"%PDF"):
                    failed += 1
                    continue
                digest = hashlib.sha256(data).hexdigest()
                if digest in seen:
                    continue
                dest.write_bytes(data)
                seen[digest] = dest
                used.add(dest.name.lower())
                copied += 1
            except urllib.error.URLError:
                failed += 1
            time.sleep(0.05)
    return {"copied": copied, "failed": failed, "composers": len(composers)}


if __name__ == "__main__":
    import json
    import sys

    DOWNLOADS.mkdir(parents=True, exist_ok=True)
    CORPUS.mkdir(parents=True, exist_ok=True)
    action = sys.argv[1] if len(sys.argv) > 1 else "all"
    result = {}
    if action in ("all", "enscore"):
        result["enscore"] = import_enscore()
    if action in ("all", "corpus"):
        result["corpus"] = download_corpus()
    print(json.dumps(result, indent=2))
