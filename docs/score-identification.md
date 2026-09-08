# Score identification and catalogue reliability

Flipper identifies score titles and composers locally from each PDF's embedded
text and metadata, with Windows OCR as a fallback. Correct attribution beats
completeness: an unknown composer is better than a confidently wrong one.

## How to inspect a decision

```powershell
dotnet run --project src/Flipper.ScoreCli -- infer "<path-to-score.pdf>" --explain
```

`--explain` prints the winning evidence and the runner-up, e.g.
`why title: Moonlight Sonata (shares 2 filename words; filename prefix)`.
`TitleVerified: false` (shown as `[unverified fallback]`) means the title is
only the readable filename, not a verified identification.

For fixtures without a PDF:

```powershell
dotnet run --project src/Flipper.ScoreCli -- infer-text tests/eval/fixtures/moonlight-sonata.json --explain
```

## How to correct a field

Corrections are stored in `.flipper-catalog.json` beside the library root with
per-field provenance (`manual`), and automatic extraction never overwrites
them — including deliberately cleared fields:

```powershell
# set the composer
dotnet run --project src/Flipper.ScoreCli -- correct <library-root> "Sub\\Piece.pdf" --composer "Ludwig van Beethoven"
# deliberately clear a wrong composer (stays empty, never re-filled by merges)
dotnet run --project src/Flipper.ScoreCli -- correct <library-root> "Sub\\Piece.pdf" --clear-composer
```

## How to preview / apply reanalysis

Dry-run first — nothing is written:

```powershell
# preview specific files (or the whole tree when no files are given)
dotnet run --project src/Flipper.ScoreCli -- preview <library-root> <pdf...> 
# re-check the reviewed state and merge (stale proposals are refused)
dotnet run --project src/Flipper.ScoreCli -- reanalyse <library-root> --apply
```

Rules:

- New PDFs are inserted with extractor version, source fingerprint and
  per-field origin (`generated` / `unresolved`).
- Changed PDFs refresh only their stale generated fields; title and composer
  improve independently.
- Manual corrections and legacy entries (no provenance) are never overwritten
  by merges. Legacy review goes through the preview path.
- Transient failures retry with bounded backoff (1m → 5m → 30m → 2h → 12h →
  daily, parking after 10 attempts); a failure is never stored as a success.

## Schema

Each catalog entry keeps its facts plus an optional `provenance` object:

```json
{
  "Sub\\Piece.pdf": {
    "title": "Moonlight Sonata",
    "subtitle": null,
    "composer": "Ludwig van Beethoven",
    "provenance": {
      "extractorVersion": 2,
      "sourceLength": 41230,
      "sourceLastWriteUtc": "2026-09-01T10:00:00Z",
      "status": "complete",
      "attempts": 1,
      "fields": {
        "title": { "origin": "generated" },
        "composer": { "origin": "manual", "explanation": "manual correction" }
      }
    }
  }
}
```

Entries without `provenance` are legacy: unknown origin, preserved as-is, and
only ever changed via preview → deliberate apply. Unknown sibling JSON fields
are preserved by every writer.

## Evaluation

- Synthetic fixtures: `tests/eval/fixtures/*.json` (16 cases across identity
  selection, credits, filenames, musical identity and extraction).
  Run: `python tests/eval/run_eval.py --cli <flipper-score.dll>`.
  Results: `tests/eval/results/before.json` → `after.json` (see §7 below).
- Real-library spot check: `tests/eval/results/real-sample-{before,after}.json`
  (50 random PDFs from the 2487-file library, before/after inferred facts).
  Synthetic-only results are never described as real-library accuracy.

## Platform checks (actually executed)

- Windows OCR: engine creation + `RecognizeAsync` succeed unpackaged on this
  host (Win11, en-GB pack present). Only en-GB/en is installed; fr/de/it/es
  packs are absent, so non-English scores fall back to embedded text plus the
  `preferred-language unavailable` detail. Verified with a throwaway WinRT
  console probe (no package identity).
- x64 build + full test suites pass (see handoff). ARM64 and installed-build
  OCR coverage: **not run** (no ARM64 device here) — not release-blocking for
  this change since OCR is best-effort fallback and every failure mode is
  observable, but should be smoke-tested on an ARM64 machine before release.
