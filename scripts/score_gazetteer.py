#!/usr/bin/env python3
from __future__ import annotations

import argparse
import gzip
import json
import re
import sys
import tarfile
import unicodedata
import urllib.request
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(r"//Alexandria/Charles/Scores")
CATALOG = ROOT / ".flipper-catalog.json"
ARTIFACTS = Path(__file__).resolve().parent.parent / "artifacts"
INDEX_PATH = ARTIFACTS / "score-gazetteer.jsonl.gz"
DUMP_DIR = ARTIFACTS / "mb-json"
REPORT_MD = ARTIFACTS / "score-gazetteer-dry-run.md"
REPORT_JSON = ARTIFACTS / "score-gazetteer-dry-run.json"
DUMP_LATEST = "https://data.metabrainz.org/pub/musicbrainz/data/json-dumps/LATEST"
DUMP_WORK = "https://data.metabrainz.org/pub/musicbrainz/data/json-dumps/{stamp}/work.tar.xz"
USER_AGENT = "Carousel-score-gazetteer/1.0 (https://github.com/seevydeepy/flipper)"

JUNK_TOKENS = {
    "pdf",
    "copy",
    "duplicate",
    "easy",
    "tutorial",
    "download",
    "downloaded",
    "musescore",
    "sheet",
    "music",
    "arr",
    "arranged",
    "transcribed",
    "transcription",
    "midi",
    "karaoke",
    "backing",
    "finale",
    "sibelius",
    "oktav",
    "8notes",
    "pdfcoffee",
    "version",
    "vocal",
    "lyrics",
    "score",
    "main",
    "theme",
    "arrangement",
}
STOP_WORDS = {
    "a", "an", "the", "and", "or", "of", "to", "in", "on", "at", "by", "for", "with", "from", "no", "nr", "book", "part",
}
LEFTOVER_JUNK = JUNK_TOKENS | {
    "piano",
    "solo",
    "guitar",
    "violin",
    "cello",
    "flute",
    "organ",
    "harp",
    "choir",
    "orchestra",
    "band",
    "duet",
    "trio",
    "quartet",
    "quintet",
    "traditional",
    "folk",
    "song",
    "hymn",
    "carol",
    "christmas",
    "advanced",
    "misc",
}
PARTICLES = {"von", "van", "de", "del", "di", "da", "la", "le", "du", "dos", "das", "der", "den", "ter", "ten", "bin", "al"}
SKIP_TYPES = {"prose", "play", "poem", "audio drama"}
COMPOSER_REL = {"composer", "writer", "songwriter"}
SITE_JUNK = re.compile(
    r"(sheetmusic[- ]?free\.com_?|sheetsdaily|thepianonotes\.com_?|"
    r"musicalibra|oktav|pdfcoffee\.com|kupdf\.net|free-scores\.com|"
    r"musescore\.com|8notes)",
    re.I,
)
PREFIX_JUNK = re.compile(r"^\[(?:free-scores\.com|oktav|musicalibra)\]_?\s*", re.I)
TRAILING_COPY = re.compile(r"\s*[\(\[]\s*\d+\s*[\)\]]\s*$")
VERSION_TAIL = re.compile(r"\s*(?:·\s*)?version\s*\d+\s*$", re.I)
HEX_TAIL = re.compile(r"[-_ ][a-f0-9]{8,}$", re.I)
DATE_PREFIX = re.compile(r"^\d{4}[-_. ]+\d{2}[-_. ]+\d{2}\s*")
LEADING_INDEX = re.compile(r"^\d{1,3}\s+-\s+")
CAMEL = re.compile(r"(?<=[a-z]{3})(?=[A-Z])")
NUMBER_BOUND = re.compile(r"(?<=[A-Za-z])(?=\d)")
TINY_SPAN = re.compile(r"^[a-z]{1,2} \d+$")
CAT_TOKEN = re.compile(r"^(bwv|hob|rv|op|k)$")
BYLINE = re.compile(r"\bby\s+(.+)$", re.I)
QUOTE_NICK = re.compile(r"[\"“']([^\"”']{3,80})[\"”']")
CATALOGUE = [
    (re.compile(r"\bbwv\s*(\d+[a-z]?)\b", re.I), lambda m: "bwv " + m.group(1).lower()),
    (re.compile(r"\b(?:k\.?\s*v\.?|kv)\.?\s*(\d+[a-z]?)\b", re.I), lambda m: "k " + m.group(1).lower()),
    (re.compile(r"\bk\.?\s*(\d{2,}[a-z]?)\b", re.I), lambda m: "k " + m.group(1).lower()),
    (re.compile(r"\brv\.?\s*(\d+)\b", re.I), lambda m: "rv " + m.group(1)),
    (re.compile(r"\bhob\.?\s*([xvi]+)\s*:?\s*(\d+)\b", re.I), lambda m: "hob " + m.group(1).lower() + " " + m.group(2)),
    (
        re.compile(r"\bop(?:us)?\.?\s*(\d+)(?:\s*(?:no\.?|n\.?|#)\s*(\d+))?", re.I),
        lambda m: "op " + m.group(1) + ((" no " + m.group(2)) if m.group(2) else ""),
    ),
]
JUNK_COMPOSER = re.compile(
    r"@|https?://|www\.|sheet music|musescore|download|photocopy|untitled|boss|creative commons",
    re.I,
)
JUNK_WHOLE_TITLE = re.compile(
    r"^(?:(?:piano|easy|guitar)\s+)?(?:arrangement|version|solo|copy)|freely|clavier|"
    r"manualiter|legatissimo|project|cresc\.?|a\s+\d+\s+clav",
    re.I,
)


def fold_marks(value: str) -> str:
    stripped = unicodedata.normalize("NFKD", value or "")
    return "".join(ch for ch in stripped if not unicodedata.combining(ch))


def words(value: str) -> list[str]:
    folded = fold_marks(value).lower().replace("'", "").replace("’", "")
    tokens = []
    for token in re.findall(r"[a-z0-9]+", folded):
        tokens.append(str(int(token)) if token.isdigit() else token)
    return tokens


def title_key(value: str) -> str:
    return " ".join(words(value))


def last_name(value: str) -> str:
    parts = [part for part in words(value) if part not in PARTICLES and len(part) >= 3]
    return parts[-1] if parts else ""


def distinctive(key: str) -> bool:
    tokens = key.split()
    if len(tokens) >= 2:
        return True
    return bool(tokens) and len(tokens[0]) >= 8


def stop_key(key: str) -> bool:
    tokens = key.split()
    return bool(tokens) and all(token in STOP_WORDS for token in tokens)


def tiny_span(key: str) -> bool:
    return bool(TINY_SPAN.fullmatch(key))


def catalogue_key(key: str) -> bool:
    tokens = key.split()
    return any(CAT_TOKEN.fullmatch(token) for token in tokens) and any(ch.isdigit() for ch in key)


def catalogues(value: str) -> set[str]:
    found = set()
    for pattern, render in CATALOGUE:
        for match in pattern.finditer(value or ""):
            found.add(render(match))
    return found


def junk_title(value: str) -> bool:
    text = value or ""
    if not text or JUNK_COMPOSER.search(text) or JUNK_WHOLE_TITLE.search(text.strip()):
        return True
    letters = sum(ch.isalpha() for ch in text)
    return letters < 3 or letters / max(len(text), 1) < 0.35


def clean_filename(name: str) -> str:
    stem = Path(name).stem
    stem = PREFIX_JUNK.sub("", stem)
    stem = DATE_PREFIX.sub("", stem)
    stem = SITE_JUNK.sub(" ", stem)
    stem = VERSION_TAIL.sub("", stem)
    stem = TRAILING_COPY.sub("", stem)
    stem = CAMEL.sub(" ", stem)
    stem = NUMBER_BOUND.sub(" ", stem)
    stem = stem.replace("_", " ").replace("-", " ")
    stem = re.sub(r"\s+", " ", stem).strip(" .-_")
    stem = HEX_TAIL.sub("", stem).strip(" -")
    stem = LEADING_INDEX.sub("", stem).strip(" -")
    return stem


def filename_tokens(cleaned: str) -> list[str]:
    return [token for token in words(cleaned) if token not in JUNK_TOKENS]


def usable_composer(value: str | None) -> str:
    text = (value or "").strip()
    if not text or JUNK_COMPOSER.search(text):
        return ""
    return text


def folder_composer(relative: str) -> str:
    parts = Path(relative.replace("\\", "/")).parts
    if len(parts) >= 2 and parts[0].lower() == "corpus":
        return parts[1]
    return ""


def extra_aliases(title: str) -> list[str]:
    found = [match.group(1).strip() for match in QUOTE_NICK.finditer(title or "")]
    blob = title or ""
    for pattern, render in CATALOGUE:
        match = pattern.search(blob)
        if match:
            found.append(render(match))
    return found


def composer_agrees(composer: str, hints: list[str]) -> bool:
    last = last_name(composer)
    if len(last) < 3:
        return False
    composer_words = {token for token in words(composer) if len(token) >= 3}
    for hint in hints:
        usable = usable_composer(hint)
        if not usable:
            continue
        hint_last = last_name(usable)
        if hint_last and hint_last == last:
            return True
        hint_words = {token for token in words(usable) if len(token) >= 3}
        if last in hint_words or (hint_last and hint_last in composer_words):
            return True
    return False


@dataclass(frozen=True, slots=True)
class Work:
    title: str
    composer: str


@dataclass(slots=True)
class Match:
    title: str
    composer: str
    key: str
    reason: str


@dataclass
class Index:
    works: list[Work] = field(default_factory=list)
    title_map: dict[str, list[int]] = field(default_factory=dict)
    unique_keys: set[str] = field(default_factory=set)

    @classmethod
    def from_records(cls, records: list[dict[str, object]]) -> "Index":
        collapsed: dict[tuple, dict[str, object]] = {}
        for index_no, record in enumerate(records):
            title = str(record.get("t") or record.get("title") or "").strip()
            composer = str(record.get("c") or record.get("composer") or "").strip()
            aliases = [str(item).strip() for item in (record.get("a") or record.get("aliases") or []) if str(item).strip()]
            key = title_key(title)
            if not key:
                continue
            cats = tuple(sorted(catalogues(title)))
            if distinctive(key) or cats:
                identity: tuple = (key, last_name(composer), cats)
            else:
                identity = (key, last_name(composer), cats, index_no)
            bucket = collapsed.get(identity)
            if bucket is None:
                collapsed[identity] = {"title": title, "composer": composer, "aliases": set(aliases)}
            else:
                bucket["aliases"].update(aliases)
                if len(title) < len(str(bucket["title"])):
                    bucket["title"] = title
                if composer and not bucket["composer"]:
                    bucket["composer"] = composer

        index = cls()
        for bucket in collapsed.values():
            work = Work(str(bucket["title"]), str(bucket["composer"]))
            wid = len(index.works)
            index.works.append(work)
            keys = {title_key(work.title)}
            keys.update(title_key(alias) for alias in bucket["aliases"] if title_key(alias))
            keys.update(title_key(alias) for alias in extra_aliases(work.title) if title_key(alias))
            for key in keys:
                if not key or stop_key(key) or tiny_span(key):
                    continue
                index.title_map.setdefault(key, []).append(wid)

        for key, ids in index.title_map.items():
            identities = {(index.works[i].title.lower(), last_name(index.works[i].composer)) for i in ids}
            if len(identities) == 1:
                index.unique_keys.add(key)
        return index

    def match(self, filename: str, composer_hints: list[str]) -> Match | None:
        cleaned = clean_filename(filename)
        hints = [hint for hint in composer_hints if usable_composer(hint)]
        folder = folder_composer(filename)
        if folder:
            hints.append(folder)
        by = BYLINE.search(cleaned)
        if by:
            hints.append(by.group(1))
            cleaned = cleaned[: by.start()].strip(" -")
        tokens = filename_tokens(cleaned)
        if not tokens:
            return None

        hits: list[tuple[int, int, int, int, str, list[int]]] = []
        for start in range(len(tokens)):
            for end in range(start + 1, len(tokens) + 1):
                key = " ".join(tokens[start:end])
                ids = self.title_map.get(key)
                if ids:
                    hits.append((end - start, -start, start, end, key, ids))
        if not hits:
            return None
        hits.sort(reverse=True)
        longest = hits[0][0]
        top = [hit for hit in hits if hit[0] == longest]
        picked = [self._pick(key, ids, hints, folder, tokens, start, end) for _, _, start, end, key, ids in top]
        found = [item for item in picked if item is not None]
        identities = {(item.title.lower(), last_name(item.composer)) for item in found}
        if len(identities) == 1:
            return found[0]
        return None

    def _pick(
        self,
        key: str,
        ids: list[int],
        hints: list[str],
        folder: str,
        tokens: list[str],
        start: int,
        end: int,
    ) -> Match | None:
        grouped: dict[object, Work] = {}
        for wid in ids:
            work = self.works[wid]
            cats = tuple(sorted(catalogues(work.title)))
            folded = title_key(work.title)
            identity: object
            if distinctive(folded) or cats:
                identity = (folded, last_name(work.composer), cats)
            else:
                identity = wid
            grouped[identity] = work
        works = list(grouped.values())
        unique = key in self.unique_keys
        if len(works) == 1:
            work = works[0]
            if unique and self._unique_title_ok(key, work, folder, hints, tokens, start, end):
                return Match(work.title, work.composer, key, "unique-title")
            if composer_agrees(work.composer, hints):
                return Match(work.title, work.composer, key, "title+composer")
            return None
        filtered = [work for work in works if composer_agrees(work.composer, hints)]
        if len(filtered) == 1:
            work = filtered[0]
            return Match(work.title, work.composer, key, "title+composer")
        return None

    def _unique_title_ok(
        self,
        key: str,
        work: Work,
        folder: str,
        hints: list[str],
        tokens: list[str],
        start: int,
        end: int,
    ) -> bool:
        if not distinctive(key) or stop_key(key) or tiny_span(key):
            return False
        if folder and work.composer and not composer_agrees(work.composer, [folder]):
            return False
        hint_words = set()
        for hint in hints:
            hint_words.update(words(hint))
            last = last_name(hint)
            if last:
                hint_words.add(last)
        matched = set(range(start, end))
        leftover = [
            token
            for i, token in enumerate(tokens)
            if i not in matched
            and not token.isdigit()
            and token not in LEFTOVER_JUNK
            and token not in hint_words
        ]
        if leftover and not catalogue_key(key) and len(key.split()) < 5:
            return False
        return True


def work_from_dump(obj: dict) -> dict[str, object] | None:
    kind = (obj.get("type") or "").strip().lower()
    if kind in SKIP_TYPES:
        return None
    title = str(obj.get("title") or obj.get("name") or "").strip()
    if not title_key(title):
        return None
    aliases = []
    for alias in obj.get("aliases") or []:
        name = str(alias.get("name") or "").strip()
        if name:
            aliases.append(name)
    aliases.extend(extra_aliases(title))
    composer = ""
    for rel in obj.get("relations") or []:
        if (rel.get("type") or "") not in COMPOSER_REL:
            continue
        artist = rel.get("artist") or {}
        name = str(artist.get("name") or artist.get("sort-name") or "").strip()
        if name:
            composer = name
            if rel.get("type") == "composer":
                break
    return {"t": title, "c": composer, "a": aliases}


def download_work_dump(dump_dir: Path) -> Path:
    dump_dir.mkdir(parents=True, exist_ok=True)
    request = urllib.request.Request(DUMP_LATEST, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(request, timeout=60) as response:
        stamp = response.read().decode("utf-8").strip()
    dest = dump_dir / f"work-{stamp}.tar.xz"
    if dest.exists() and dest.stat().st_size > 1_000_000:
        return dest
    url = DUMP_WORK.format(stamp=stamp)
    tmp = dest.with_suffix(dest.suffix + ".tmp")
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(request, timeout=120) as response, tmp.open("wb") as handle:
        while True:
            chunk = response.read(1024 * 1024)
            if not chunk:
                break
            handle.write(chunk)
    tmp.replace(dest)
    return dest


def compile_index(dump_path: Path, index_path: Path) -> int:
    index_path.parent.mkdir(parents=True, exist_ok=True)
    count = 0
    tmp = index_path.with_name(index_path.name + ".tmp")
    with tarfile.open(dump_path, "r:xz") as archive, gzip.open(tmp, "wt", encoding="utf-8") as out:
        for member in archive:
            if not member.isfile():
                continue
            name = member.name.replace("\\", "/").rstrip("/")
            if "mbdump" in name and not name.endswith("work"):
                continue
            handle = archive.extractfile(member)
            if handle is None:
                continue
            for raw in handle:
                line = raw.decode("utf-8", errors="replace").strip()
                if not line:
                    continue
                try:
                    obj = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if not isinstance(obj, dict):
                    continue
                if "title" not in obj and "name" not in obj and isinstance(obj.get("work"), dict):
                    obj = obj["work"]
                record = work_from_dump(obj)
                if not record:
                    continue
                out.write(json.dumps(record, ensure_ascii=False) + "\n")
                count += 1
                if count % 100000 == 0:
                    print(count, file=sys.stderr, flush=True)
    tmp.replace(index_path)
    return count


def load_index(index_path: Path) -> Index:
    records: list[dict[str, object]] = []
    with gzip.open(index_path, "rt", encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            records.append(json.loads(line))
    return Index.from_records(records)


def load_catalog(path: Path) -> dict[str, dict[str, str]]:
    return json.loads(path.read_text(encoding="utf-8"))


def refuse_catalog_write(path: Path) -> None:
    if path.name.lower() == CATALOG.name.lower():
        raise SystemExit(f"refusing to write catalog path: {path}")


def proposed_facts(current: dict[str, str], matched: Match) -> dict[str, str]:
    title = matched.title
    composer = matched.composer or usable_composer(current.get("composer"))
    return {
        "title": title,
        "subtitle": current.get("subtitle") or "",
        "composer": composer or "",
    }


def same_facts(left: dict[str, str], right: dict[str, str]) -> bool:
    return title_key(left.get("title") or "") == title_key(right.get("title") or "") and last_name(
        left.get("composer") or ""
    ) == last_name(right.get("composer") or "")


def should_update(current: dict[str, str], proposed: dict[str, str]) -> bool:
    if same_facts(current, proposed):
        return False
    current_title = current.get("title") or ""
    if junk_title(current_title):
        return True
    current_tokens = {token for token in title_key(current_title).split() if token not in STOP_WORDS}
    proposed_tokens = {token for token in title_key(proposed.get("title") or "").split() if token not in STOP_WORDS}
    if proposed_tokens and proposed_tokens < current_tokens:
        return False
    if current_tokens and proposed_tokens and current_tokens.isdisjoint(proposed_tokens):
        return False
    current_cats = catalogues(current_title)
    proposed_cats = catalogues(proposed.get("title") or "")
    if current_cats and current_cats & proposed_cats:
        current_last = last_name(current.get("composer") or "")
        proposed_last = last_name(proposed.get("composer") or "")
        if not current_last or current_last == proposed_last:
            return False
    return True


def dry_run(catalog: dict[str, dict[str, str]], index: Index) -> dict[str, object]:
    updates = []
    matched = 0
    collisions = 0
    for relative, facts in catalog.items():
        hints = [facts.get("composer") or "", folder_composer(relative)]
        found = index.match(relative, hints)
        if found is None:
            tokens = filename_tokens(clean_filename(relative))
            had_hit = False
            for start in range(len(tokens)):
                for end in range(start + 1, len(tokens) + 1):
                    if index.title_map.get(" ".join(tokens[start:end])):
                        had_hit = True
                        break
                if had_hit:
                    break
            if had_hit:
                collisions += 1
            continue
        matched += 1
        nxt = proposed_facts(facts, found)
        if not should_update(facts, nxt):
            continue
        updates.append(
            {
                "path": relative,
                "current_title": facts.get("title") or "",
                "current_composer": facts.get("composer") or "",
                "proposed_title": nxt["title"],
                "proposed_composer": nxt["composer"],
                "reason": found.reason,
                "key": found.key,
            }
        )
    return {
        "catalog_entries": len(catalog),
        "index_works": len(index.works),
        "unique_keys": len(index.unique_keys),
        "matched": matched,
        "unchanged_matches": matched - len(updates),
        "updates": len(updates),
        "ambiguous_skips": collisions,
        "rows": updates,
    }


def write_report(result: dict[str, object], md_path: Path, json_path: Path) -> None:
    md_path.parent.mkdir(parents=True, exist_ok=True)
    rows = result["rows"]
    lines = [
        "# Score gazetteer dry-run",
        "",
        f"- Catalog entries: {result['catalog_entries']}",
        f"- Index works: {result['index_works']}",
        f"- Unique title keys: {result['unique_keys']}",
        f"- Gazetteer matches: {result['matched']}",
        f"- Matches already agreeing: {result['unchanged_matches']}",
        f"- Proposed updates: {result['updates']}",
        f"- Ambiguous title hits skipped: {result['ambiguous_skips']}",
        "",
        "No catalog writes were made.",
        "",
        "| Path | Current title | Current composer | Proposed title | Proposed composer | Reason |",
        "| --- | --- | --- | --- | --- | --- |",
    ]
    for row in rows:
        def cell(value: str) -> str:
            return (value or "").replace("|", "\\|").replace("\n", " ")

        lines.append(
            "| "
            + " | ".join(
                cell(row[key])
                for key in (
                    "path",
                    "current_title",
                    "current_composer",
                    "proposed_title",
                    "proposed_composer",
                    "reason",
                )
            )
            + " |"
        )
    md_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    json_path.write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding="utf-8")


def cmd_compile(args: argparse.Namespace) -> int:
    dump = Path(args.dump) if args.dump else download_work_dump(DUMP_DIR)
    refuse_catalog_write(Path(args.index))
    count = compile_index(dump, Path(args.index))
    print(json.dumps({"records": count, "dump": str(dump), "index": args.index}, indent=2))
    return 0


def cmd_dry_run(args: argparse.Namespace) -> int:
    catalog = load_catalog(Path(args.catalog))
    index = load_index(Path(args.index))
    refuse_catalog_write(Path(args.report))
    refuse_catalog_write(Path(args.json_report))
    result = dry_run(catalog, index)
    write_report(result, Path(args.report), Path(args.json_report))
    summary = {key: result[key] for key in result if key != "rows"}
    summary["report"] = args.report
    summary["json_report"] = args.json_report
    print(json.dumps(summary, indent=2))
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="cmd", required=True)
    compile_p = sub.add_parser("compile")
    compile_p.add_argument("--dump", default="")
    compile_p.add_argument("--index", default=str(INDEX_PATH))
    compile_p.set_defaults(func=cmd_compile)
    dry_p = sub.add_parser("dry-run")
    dry_p.add_argument("--catalog", default=str(CATALOG))
    dry_p.add_argument("--index", default=str(INDEX_PATH))
    dry_p.add_argument("--report", default=str(REPORT_MD))
    dry_p.add_argument("--json-report", default=str(REPORT_JSON))
    dry_p.set_defaults(func=cmd_dry_run)
    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
