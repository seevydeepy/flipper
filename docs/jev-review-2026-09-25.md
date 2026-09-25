# Jev feature review — 25 September 2026

Reviewed the complete Jev addition from `424d53f`, including candidate proposals,
the TypeSafe request/response, Windows key storage, extraction, catalogue merges,
background scheduling, Settings and the score editor.

## Findings fixed

- Settings used a two-line, ellipsis-limited value control for the long help text.
  The explanation now lives in a keyboard-accessible **?** flyout. Key controls
  fill the available width; job and error messages wrap without a line limit.
- Reanalysis reported only its initial queue count. Service errors were swallowed,
  and closing Settings discarded the message. The catalogue service now owns
  progress, completion counts and the last error. Failed writes retain pending
  results and offer a save retry. A deliberate request has its own queue entry,
  so an automatic extraction already in flight or awaiting a save cannot consume it.
- An unknown remote choice below the confidence threshold was accepted as an
  abstention, which could clear generated catalogue facts. Candidate identity is
  now validated before the confidence threshold is applied.

The requested single-score **AI** action reuses the extractor and previews
confident suggestions in the editor. Saving uses the existing manual correction
path; closing cancels the request and does not apply suggestions.

## Review evidence

The [official TypeSafe API reference](https://docs.typesafe.ai/api) matches the
endpoint, bearer authentication, state/questions body and choice answer shape.
The saved key is confined to Windows Credential Locker and the request header.
Automatic and batch merges still preserve manual and legacy values, including
deliberately cleared fields. Fields not evaluated by Jev keep their existing
values during a forced refresh. Existing lifecycle tests cover these contracts.

An independent static ACP review used Muse Spark 1.3 Contributor at `xhigh`
against `424d53f..a0e4f20` and returned `NO_BLOCKER`. The review checkout and
integration checkout stayed unchanged, and the reviewer process was cleaned up.

## Validation

```powershell
dotnet test tests/Flipper.Core.Tests/Flipper.Core.Tests.csproj -c Release
dotnet build src/Flipper.App/Flipper.App.csproj -c Release
```

286 tests passed. The x64 Windows app built with no warnings or errors.
New request tests verify the endpoint, header, transmitted evidence and omitted
questions; invalid-choice tests verify rejection instead of destructive abstention.

Windows UI checks used a disposable build, four synthetic PDFs, separate settings
and cache, an inert test key and delayed in-process HTTP replies. They did not use
the user's key or scores. Checks covered:

- The full help text in the flyout and Settings at 100% and 200% scale.
- A running batch after closing and reopening Settings, and retained final results.
- Exactly three batch requests: one match, one abstention and one HTTP 401 error;
  the legacy score was skipped. The failed score retained its catalogue facts.
- One-score matching on the skipped legacy score, disabled editing while busy,
  visible suggestions, no write before Save, and preservation of its subtitle.
- Save recorded only the changed title and composer as manual. Cancel during a
  later match left the catalogue SHA-256 unchanged.
- Holding the synthetic catalogue's write lock produced a paused message and
  **Retry catalogue save**. Releasing it and retrying saved the retained result
  and resumed the queue without repeating that score's HTTP request.

To repeat the user flow on a disposable library: save a test key, run reanalysis,
close and reopen Settings during and after the run, then use a card's pencil
button and **AI**. Check both Save and Cancel and inspect the catalogue results.
Use simulated replies for service-error tests so no real score data is sent.

Live TypeSafe authentication and recognition accuracy were not tested. The fixed
confidence thresholds remain uncalibrated against this user's score library.
ARM64 and Windows 10 tablet UI acceptance were not run. Progress survives menu
closure within the current app session; it is not persisted across app restarts.
