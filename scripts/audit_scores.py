#!/usr/bin/env python3
"""Read PDFs under Scores and write .flipper-catalog.json with title and composer."""

from __future__ import annotations

import json
import os
import re
import sys
from pathlib import Path

import pymupdf

ROOT = Path(r"//Alexandria/Charles/Scores")
CATALOG = ROOT / ".flipper-catalog.json"
BACKUP = Path(__file__).resolve().parent.parent / "artifacts" / ".flipper-catalog.json"

JUNK = re.compile(
    r"public domain|creative commons|mutopia|typeset|licensed under|reference:|"
    r"free to download|creativecommons|copyright|this sheet music|"
    r"sheet music from www|unsaved publication|www\.|https?://|finale \d|"
    r"untitled\d*|created o[nm]",
    re.I,
)
TEMPO = re.compile(
    r"^(moderato|andante|allegro|allegretto|adagio|largo|presto|vivace|swing|"
    r"maestoso|andantino|rubato|a tempo|rit\.?)$",
    re.I,
)
GARBAGE = re.compile(r"^(.)\1{2,}$|\\u[eE]|[^\x00-\x7F]{2,}")
COLLECTION = re.compile(
    r"^\d+\s*(\(\d+\))?\s+(pi[eè]ces|studies|etudes|études|duets|lessons|caprices|exercises|airs)\b",
    re.I,
)
MEASURE = re.compile(r"^\d{1,3}[a-z]?$")
META_JUNK = re.compile(r"^(title|untitled|document|lg-\d+|microsoft word)$", re.I)
BAD_ROLE = re.compile(
    r"^(pedal|piano|basso|violino|viola|cello|flute|guitar|soprano|alto|tenor|"
    r"bass|tema|andantino|allegro|andante|adagio|hob\.|op\.|bwv|arr\.|"
    r"sheet music|solo|trombone|trumpet|violin|oboe|utente)$",
    re.I,
)
BYLINE = re.compile(r"^(?:by|arr\.?|arranged by|transc(?:ribed)?\.? by|composed by|music by)\s+(.+)$", re.I)
YEARS = re.compile(r"\s*\(\s*\d{3,4}(?:\s*[-–]\s*\d{2,4})?\s*\)\s*")
COPY_NUM = re.compile(r"\s*\(\d+\)\s*$")
BRACKETS = re.compile(r"\s*\[[^\]]*\]")
FINALE = re.compile(r"finale\s+\d+.*\[(.+)\]", re.I)
WHOLE_PAREN = re.compile(r"^\(([^()]*)\)\s*$")
TRAILING_PAREN = re.compile(r"^(.*[^\s(])\s*\(([^()]*)\)\s*$")
PIECE = re.compile(
    r"^(?:(?:main|love|end|opening|closing)\s+)?theme(?:\s+from\b.*)?$|^from\b.+$|^(?:piano\s+)?version$",
    re.I,
)
DIRECTION = re.compile(
    r"^(?:(?:\d+\s+)?times|forte|piano|pianissimo|fortissimo|alio modo|ad lib\.?|repeat)$",
    re.I,
)
TITLE_GLUE = {"on", "of", "the", "and", "from", "to", "in", "at", "for", "by", "with", "is", "a", "an"}
STOP = {"the", "and", "for", "from", "with", "pdf", "piano", "solo", "arr", "sheet", "music"}

_OCR = None


def ocr_engine():
    global _OCR
    if _OCR is False:
        return None
    if _OCR is None:
        try:
            from rapidocr_onnxruntime import RapidOCR

            _OCR = RapidOCR()
        except Exception:
            _OCR = False
            return None
    return _OCR


def clean(text: str) -> str:
    text = "".join(ch if ch >= " " else " " for ch in text)
    text = text.replace("•", " ").replace("©", " ")
    text = text.replace("（", "(").replace("）", ")")
    text = re.sub(r"\s+", " ", text).strip(" \u00a0-–_|")
    return text


def tokens(value: str) -> set[str]:
    folded = (value or "").lower().replace("'", "").replace("’", "")
    words = re.findall(r"[a-z]{3,}", folded)
    return {word for word in words if word not in STOP}


def agrees(left: str, right: str) -> bool:
    return bool(tokens(left) & tokens(right))


def tidy_title(value: str) -> str:
    text = clean(value)
    finale = FINALE.search(text)
    if finale:
        text = finale.group(1)
        text = re.sub(r"\.(mus|mscz|mscx)$", "", text, flags=re.I)
    text = BRACKETS.sub("", text)
    text = COPY_NUM.sub("", text)
    text = YEARS.sub(" ", text)
    text = re.sub(r"\s+", " ", text).strip(" -–_|\"'`")
    return text


def is_bad_title(value: str) -> bool:
    if not value or len(value) < 3 or JUNK.search(value) or COLLECTION.search(value):
        return True
    if "[" in value or "]" in value:
        return True
    if re.fullmatch(r"\d+", value):
        return True
    if re.match(r"^(imslp|mn0|lg-)\d", value, re.I):
        return True
    if re.fullmatch(r"bwv\s*-?\s*\d+[a-z]?", value, re.I):
        return True
    if BAD_ROLE.search(value) or TEMPO.search(value) or GARBAGE.search(value):
        return True
    if value.lower() in STOP:
        return True
    letters = [ch.lower() for ch in value if ch.isalpha()]
    if letters:
        vowels = sum(ch in "aeiouy" for ch in letters)
        if len(letters) >= 3 and vowels == 0:
            return True
        if len(letters) <= 4 and len(set(letters)) <= 2:
            return True
    letters = sum(ch.isalpha() for ch in value)
    return letters < 3 or letters / max(len(value), 1) < 0.35


def useful(line: str) -> bool:
    if not line or MEASURE.match(line) or JUNK.search(line) or BAD_ROLE.search(line):
        return False
    letters = sum(ch.isalpha() for ch in line)
    return letters >= 3 and letters / max(len(line), 1) >= 0.45


def meta_ok(value: str | None) -> str:
    value = tidy_title(value or "")
    if not value or META_JUNK.search(value) or is_bad_title(value):
        return ""
    return value


def looks_like_name(value: str) -> bool:
    text = YEARS.sub("", value).strip()
    if not text or WHOLE_PAREN.match(text):
        return False
    letters = [ch for ch in text if ch.isalpha()]
    if len(letters) >= 2 and all(ch.isupper() for ch in letters) and len(text.split()) >= 2:
        return False
    if re.search(r"[A-Za-z]['’]s\b", text):
        return False
    words = [word for word in text.split() if word]
    if not 2 <= len(words) <= 6:
        return False
    if any(word.lower() in {"piano", "solo", "arr", "opus", "op."} for word in words):
        return False
    if any(word.lower() in TITLE_GLUE for word in words):
        return False
    caps = sum(word[:1].isupper() for word in words if word[:1].isalpha())
    return caps >= max(1, len(words) - 1)


def folder_composer(rel: Path) -> str:
    parts = rel.parts
    if len(parts) >= 2 and parts[0].lower() == "corpus":
        return parts[1]
    return ""


def page_text(pdf: Path) -> str:
    try:
        doc = pymupdf.open(pdf)
    except Exception:
        return ""
    try:
        if not doc.page_count:
            return ""
        text = doc[0].get_text("text")
        letters = sum(ch.isalpha() for ch in text)
        if letters >= 20:
            return text
        engine = ocr_engine()
        if engine is None:
            return text
        pix = doc[0].get_pixmap(matrix=pymupdf.Matrix(2, 2), alpha=False)
        result, _ = engine(pix.tobytes("png"))
        if not result:
            return text
        return "\n".join(row[1] for row in result if row and row[1])
    finally:
        doc.close()


def lines_from_text(text: str) -> list[str]:
    found = []
    for raw in text.splitlines():
        line = clean(raw)
        if useful(line):
            found.append(line)
        if len(found) >= 10:
            break
    return found


def fold_words(value: str) -> list[str]:
    folded = (value or "").lower().replace("'", "").replace("’", "")
    return [word for word in re.findall(r"[a-z]+", folded) if word not in STOP]


def prefix_bonus(title: str, file_title: str) -> int:
    file_words = fold_words(file_title)
    title_words = fold_words(title)
    return int(bool(title_words) and file_words[: len(title_words)] == title_words)


def unwrap_whole(value: str) -> str:
    match = WHOLE_PAREN.match((value or "").strip())
    return match.group(1).strip() if match else ""


def is_piece(value: str) -> bool:
    inner = unwrap_whole(value) or value
    return bool(PIECE.match(inner.strip()))


def is_direction(value: str) -> bool:
    inner = unwrap_whole(value) or value
    return bool(DIRECTION.match(inner.strip()))


def split_dash(value: str) -> tuple[str, str]:
    parts = re.split(r"\s+[-–—]\s+", value, maxsplit=1)
    if len(parts) == 2:
        return parts[0].strip(), parts[1].strip()
    return value, ""


def pick_page_title(lines: list[str], file_title: str) -> str:
    scored = []
    for line in lines:
        title = tidy_title(line)
        if is_bad_title(title) or is_direction(title):
            continue
        inner = unwrap_whole(title) or title
        scored.append((
            0 if unwrap_whole(title) else 1,
            0 if is_piece(title) else 1,
            0 if looks_like_name(inner) else 1,
            len(tokens(inner) & tokens(file_title)),
            prefix_bonus(inner, file_title),
            title,
        ))
    if not scored:
        return ""
    overlapping = [row for row in scored if row[3] > 0]
    pool = overlapping or scored
    pool.sort(key=lambda row: (row[0], row[1], row[2], row[3], row[4]), reverse=True)
    return pool[0][5]


def pick_headings(lines: list[str], file_title: str) -> tuple[str, str]:
    title = pick_page_title(lines, file_title)
    subtitle = ""
    inner = unwrap_whole(title)
    if inner:
        if is_piece(inner):
            subtitle = inner
            others = [line for line in lines if tidy_title(line) != title]
            title = pick_page_title(others, file_title) or inner
        else:
            title = inner

    trailing = TRAILING_PAREN.match(title)
    if trailing:
        extra = trailing.group(2).strip()
        if extra and not is_direction(extra):
            title = trailing.group(1).strip()
            subtitle = subtitle or extra

    if not subtitle:
        for line in lines:
            candidate = tidy_title(line)
            wrapped = unwrap_whole(candidate)
            if not wrapped or wrapped.lower() == title.lower() or is_direction(wrapped):
                continue
            subtitle = wrapped
            break

    return tidy_title(title), re.sub(r"\s+", " ", subtitle).strip(" -–_|")


def extract(pdf: Path, rel: Path) -> dict[str, str]:
    try:
        doc = pymupdf.open(pdf)
        meta = doc.metadata or {}
        doc.close()
    except Exception:
        meta = {}

    text = page_text(pdf)
    page_lines = lines_from_text(text)
    file_title = tidy_title(pdf.stem)
    folder = folder_composer(rel)

    meta_title = meta_ok(meta.get("title"))
    composer = meta_ok(meta.get("author"))
    if composer and meta_title and composer.lower() == meta_title.lower():
        composer = ""
    if meta_title and not (agrees(meta_title, file_title) or agrees(meta_title, " ".join(page_lines[:4]))):
        meta_title = ""

    page_title, subtitle = pick_headings(page_lines, file_title)
    if not page_title:
        page_title, subtitle = pick_headings([file_title], file_title)
    title = page_title or meta_title or file_title
    if not subtitle:
        _, extra = pick_headings([meta_title or title], file_title)
        subtitle = extra

    if page_lines:
        first = page_lines[0]
        byline = BYLINE.match(first)
        if byline or looks_like_name(first) or (folder and folder.lower() in first.lower()):
            if not unwrap_whole(first) and not is_piece(first):
                composer = composer or tidy_title(byline.group(1) if byline else first)

    if not composer:
        for line in page_lines:
            match = BYLINE.match(line)
            if match:
                composer = tidy_title(match.group(1))
                break
            if looks_like_name(line) and line.lower() != title.lower() and not unwrap_whole(line) and not is_piece(line):
                composer = tidy_title(line)
                break

    if not composer:
        by_file = re.search(r"\bby\s+(.+)$", file_title, flags=re.I)
        if by_file:
            composer = tidy_title(by_file.group(1))
    if not composer:
        composer = folder

    if composer and (
        BAD_ROLE.search(composer)
        or JUNK.search(composer)
        or composer.lower() == title.lower()
        or re.fullmatch(r"(?:music|composed|arranged)\s+by", composer, flags=re.I)
    ):
        composer = folder

    if is_bad_title(title):
        title = file_title
    if "[" in title or "]" in title:
        title = tidy_title(title)
    if is_bad_title(title):
        title = file_title

    if looks_like_name(title):
        file_words = file_title.split()
        name_words = title.split()
        count = len(name_words)
        if count:
            file_fold = [word.lower() for word in file_words]
            name_fold = [word.lower() for word in name_words]
            if file_fold[-count:] == name_fold:
                composer = composer or title
                title = " ".join(file_words[:-count]) or title
            elif file_fold[:count] == name_fold:
                composer = composer or title
                title = " ".join(file_words[count:]) or title

    if len(title) <= 4 and not agrees(title, file_title):
        title = file_title

    title = tidy_title(title)
    composer = tidy_title(composer)
    composer = re.sub(r"^(?:performer|artist|composer|arranger)\s*:\s*", "", composer, flags=re.I).strip()
    if title and composer and title.lower() in composer.lower():
        _, maybe = split_dash(composer)
        if maybe:
            composer = maybe
    if composer.lower() == title.lower() or (title and title.lower() in composer.lower() and not looks_like_name(composer)):
        composer = folder

    return {"title": title[:160], "subtitle": subtitle[:160], "composer": composer[:80]}


def write_catalog(path: Path, catalog: dict[str, dict[str, str]]) -> None:
    payload = json.dumps(catalog, indent=2, ensure_ascii=False)
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(path.name + ".tmp")
    tmp.write_text(payload, encoding="utf-8")
    os.replace(tmp, path)


def main() -> int:
    catalog = {}
    errors = 0
    ocr_used = 0
    for pdf in sorted(ROOT.rglob("*.pdf")):
        rel = pdf.relative_to(ROOT)
        try:
            catalog[str(rel).replace("/", "\\")] = extract(pdf, rel)
        except Exception:
            errors += 1
            catalog[str(rel).replace("/", "\\")] = {"title": tidy_title(pdf.stem), "subtitle": "", "composer": folder_composer(rel)}

    write_catalog(BACKUP, catalog)
    nas_error = ""
    try:
        write_catalog(CATALOG, catalog)
        dest = str(CATALOG)
    except OSError as exc:
        nas_error = str(exc)
        dest = str(BACKUP)

    named = sum(1 for item in catalog.values() if item.get("composer"))
    brackets = sum(1 for item in catalog.values() if "[" in (item.get("title") or "") or "]" in (item.get("title") or ""))
    print(json.dumps({
        "files": len(catalog),
        "with_composer": named,
        "errors": errors,
        "titles_with_brackets": brackets,
        "path": dest,
        "backup": str(BACKUP),
        "nas_error": nas_error,
    }, indent=2))
    return 1 if nas_error else 0


if __name__ == "__main__":
    sys.exit(main())
