# Score identification and catalogue reliability

Flipper identifies score titles and composers locally from each PDF's embedded
text and metadata, with Windows OCR as a fallback. Correct attribution beats
completeness: an unknown composer is better than a confidently wrong one.

## Automatic extraction

The library loads catalogue facts and provenance together on a background
thread. Scheduling uses that snapshot in memory. Catalogue updates refresh
card labels in batches without a directory scan or a folder-tree rebuild.

One worker inspects the first page's embedded text. It reads up to two more
pages only if the title is unresolved. OCR runs only when the embedded text
is sparse or extraction fails; a missing composer alone does not trigger it.
OCR stops after an identified title, with a maximum of two pages. The 30-second
budget is cooperative: a native PDF call already in progress cannot be stopped.
Extraction pauses when the library is left, and reader rendering runs off the
UI thread so it can wait for the shared PDF renderer without blocking input.

Printed text must have title evidence, such as filename agreement or a prominent
heading. Metadata alone, generic text and ambiguous candidates are not verified
titles. Unverified filenames remain display fallbacks and are not saved as
identified titles. Composer names need a role label or corroboration; separate
title and composer columns are kept separate. App and CLI use the same rich
inference decision, including its evidence.

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
them — including deliberately cleared fields.

Use the pencil button on a catalogue card to edit its title, subtitle and
composer. Only changed fields become manual corrections. The CLI offers the
same correction path:

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
- Missing fields after a successful inspection are stable unknowns. They do
  not cause repeated extraction. An extractor-version change does not start
  automatic reanalysis of the whole library; use the preview/apply path above.
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
      "extractorVersion": 3,
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
  Historical results: `tests/eval/results/before.json` → `after.json`.
  These text-only fixtures measure returned labels, not layout, OCR or verified
  identity. Some expect guesses now deliberately left unresolved. Core tests
  separately cover abstention, rich layout, manual fields and retry deadlines.
- Real-library spot check: `tests/eval/results/real-sample-{before,after}.json`
  (50 random PDFs from the 2487-file library, before/after inferred facts).
  Synthetic-only results are never described as real-library accuracy.

## Earlier platform checks

- Windows OCR: engine creation + `RecognizeAsync` succeed unpackaged on this
  host (Win11, en-GB pack present). Only en-GB/en is installed; fr/de/it/es
  packs are absent, so non-English scores fall back to embedded text plus the
  `preferred-language unavailable` detail. Verified with a throwaway WinRT
  console probe (no package identity).
- x64 build + full test suites pass (see handoff). ARM64 and installed-build
  OCR coverage: **not run** (no ARM64 device here) — not release-blocking for
  this change since OCR is best-effort fallback and every failure mode is
  observable, but should be smoke-tested on an ARM64 machine before release.

The September 2026 performance fix was checked on x64 Windows 11 with synthetic
digital and image-only PDFs through the actual app extraction and scheduling
services. This is not a Windows 10 tablet acceptance test or a new real-library
accuracy measurement. Native-call interruption would require a separate process;
that remains outside this change.
