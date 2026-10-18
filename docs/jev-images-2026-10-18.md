# Jev image fallback: investigation and validation

## Decision

TypeSafe's [image documentation](https://docs.typesafe.ai/images), checked on
18 October 2026, allows images with the pinned Jev 1.13 model. The API is
experimental. Page layout can provide extra evidence for an uncertain title or
composer choice, so Carousel now offers an explicit opt-in fallback. It defaults
to off and uses only the first two pages, with one extra request at most per
extraction. It keeps the existing local candidates and acceptance thresholds.

The [confidence documentation](https://docs.typesafe.ai/confidence) confirms that
Choice confidence is derived from option probabilities. Carousel continues to
use its existing selected-option thresholds; this change does not recalibrate
them. Confident matches and confident `none` answers do not trigger image uploads.
The current behaviour and limits are described in [score identification](score-identification.md).

## Checks

- All 302 core tests passed. The 16 image tests cover multipart request structure,
  per-field selection, thresholds, rendering/upload failures, size/page limits,
  cancellation and opting out during preparation. Settings tests cover the
  persisted preference and default-off behaviour for existing settings files.
- The x64 WinUI Debug build passed with zero warnings and errors.
- A disposable Windows console probe used the actual page renderer and extractor,
  with synthetic PDFs and a substituted HTTP handler/key store. Portrait,
  landscape, rotated, wide and small pages produced decodable JPEGs within the
  dimension and byte limits. A three-page PDF sent at most two pages. Extreme
  aspect ratios were rejected before rendering; permission was rechecked after
  rendering.
- Extractor checks covered disabled images, successful image refinement, confident
  text, failed text requests, failed image requests, the extraction time limit,
  revoked permission and caller cancellation. Text results survived image failure
  and timeout; caller cancellation propagated.
- Catalogue service checks confirmed that image totals/warnings survive saving
  and pause/resume, manual composer corrections remain intact, and completing a
  job does not repeat its upload.
- The isolated Windows app used a synthetic library and separate settings/cache,
  with real credential access disabled. The new control and help panel were
  inspected at 100% and 200% UI scale. Controls remained readable and the settings
  content scrolled at the larger scale.

An independent static review used Claude Opus 5.5 at high effort through the ACP
review route. It found no blocker. Its one observation was corrected: withdrawing
image permission during rendering now skips the image pass without recording a
failure. The 16 image tests and app build passed again after that correction.
The review checkout and integration checkout remained unchanged during review,
and the reviewer process was cleaned up.

No real scores or API keys were used, and no requests were sent to TypeSafe during
validation. The live experimental endpoint and improvement in real-score accuracy
remain unverified. ARM64 was not tested. Native PDF calls retain the existing
cooperative cancellation limit.
