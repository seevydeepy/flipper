# Carousel

Carousel is a Windows app for reading sheet music off a screen without touching
anything. You point it at a folder of PDFs, it builds a library, and you turn
pages by voice — use words you choose and calibrate — while your hands are busy
holding an instrument. Page turns are gesture- and key-driven too, so it works
fine without a microphone.

It's called Flipper inside the source tree because that was the working name and
renaming everything felt like a waste of a perfectly good afternoon.

## What it does

- **Library** — scans a folder (local or UNC) for PDFs, keeps a catalog in a
  sidecar next to the scores, and does search, folders, favourites, playlists,
  and a trash bin. Metadata lives beside the music, so moving the folder
  somewhere else doesn't lose any of it.
- **Reader** — proper page rendering with paper-white theming, ink annotations
  cropping, page-turn gestures and keys.
- **Voice** — local matching against your own recorded command sounds. In
  Settings under **Voice turning**, add one or more words for **Next page**,
  **Previous page**, **First page** or **Back to library**. Each word needs its
  own calibration. Its label is for display; the sound profile selects the
  action, without a fixed vocabulary or text recognition.
  Each action shows **Ready**, **Not calibrated** or **No word yet**, and each
  word shows whether it is ready for the selected microphone.
  **Add and calibrate** opens a window with four stages: **Quiet room**,
  **Playing**, **Your word** (three recordings) and **Test** (fresh quiet and
  playing sound, then the word twice). The stage row and step count show where
  you are. Press **Record** for each of the nine steps; each step confirms when
  it passes. If a recording is not usable (no sound, two sounds or distorted
  audio), **Try again** repeats only that step. If a usable recording fails, for
  example the word is not recognised or the background is mistaken for it, the
  three word recordings are made again and the room and playing sound are kept.
  Switching to another window stops the current recording and the microphone;
  finished steps are kept. **Save calibration** is offered after all tests
  pass. Use your normal voice, distance and
  microphone position and background conditions when recording the commands.
  A change in background noise can require recalibration. Commands need a short pause before and after the word;
  continuous sound can prevent detection. Similar sounds assigned to different
  actions are rejected during calibration.
  Each profile contains acoustic features and a match threshold. These stay on
  this device and apply only to the microphone used for calibration, including
  when selected through **System default**. Raw recordings stay in memory and
  are not saved or uploaded. Failed or cancelled calibration keeps the previous
  profile. The old fixed words and sensitivity-only calibration are replaced;
  add and calibrate the words you want to use. This learns examples of your
  commands, not a general speech or accent model.
- **Winks** — optional camera page turns, enabled in Settings under **Wink
  turning**. Wink your right eye to move forward or your left eye to move back,
  then reopen both eyes. Settings shows **Ready**, **Not calibrated**, **Not
  calibrated for this camera** or **No camera found**. **Calibrate** opens a
  separate window with three stages: open eyes, collect samples and final
  checks. Winks and blinks are counted automatically; each one fills a dot, and
  a message confirms each passed step. When the samples cannot yet separate
  winks from blinks, or a check does not pass, the window says why and starts
  an extra round that asks only for the gestures that need more samples. A
  wink of the wrong eye, or a one-eye closure when a blink is asked for, is
  not counted, and the window says what to do instead; the fourth such
  gesture in a row is counted. A sample that cannot
  be scored and blocks every fit is left out. After three extra rounds the
  calibration stops with a tip, keeps the previous calibration, and offers
  **Start again** with new samples. Save after all checks pass. Use the
  large **Pause** button below the prompts to take a break at any point, and
  **Resume** to continue from the same step. Stage,
  round and sample counts show progress when extra samples are needed.
  A circular meter beside the preview shows calibration progress, reaching
  100% when the profile is ready to save. Calibration allows occasional missed
  winks, but rejects page turns during blink and open-eye checks, wrong-direction
  turns and extra turns. New gestures check the fitted profile before saving.
  Calibration can allow the other eye to partly close when your winks can
  still be separated from blinks. Your profile stays on the device across
  restarts and updates; **Recalibrate** replaces it only after a successful save.
  The camera runs while the reader or calibration window is active. Images
  are processed locally and are not saved or sent anywhere.
  This is off by default and needs an available camera with Windows camera
  access enabled. If capture is unavailable, the other page controls still work.
- **Auto-update** — checks GitHub Releases, downloads the new version, installs
  it in place, relaunches. First install comes straight from GitHub too.

## Download

Grab the latest asset from
[Releases](https://github.com/seevydeepy/flipper/releases):

- `Carousel.Setup-<version>-win-x64.exe` / `-win-arm64.exe` — installer
- `Carousel-win-x64.zip` / `Carousel-win-arm64.zip` — portable, just unzip and run

Windows 10/11, x64 or ARM64. Builds are self-contained, no runtime install
needed.

## Verifying builds

Every push to `main` is built on GitHub Actions and the four release assets get
build provenance attestations (SLSA-style, signed by GitHub's Sigstore Fulcio).
To check a downloaded file:

```text
gh attestation verify Carousel.Setup-1.x.x-win-x64.exe -R seevydeepy/flipper
```

## Building it yourself

.NET 9 SDK (`global.json` pins the minimum). Python is needed for the icon
stamping scripts that `publish.ps1` uses; plain building and testing isn't.
Publishing downloads pinned Microsoft Visual C++ redistributable packages and
copies the required x64 or ARM64 runtime DLLs beside `Carousel.exe`. The package
URLs and SHA-256 hashes are in `scripts/vc-runtime.json`; no separate runtime
installation is needed on the user's PC. These Microsoft files retain their
original signatures and are covered by Microsoft's redistribution terms.

```text
dotnet test Flipper.sln -c Release
dotnet run --project src/Flipper.App/Flipper.App.csproj
pwsh -File scripts/publish.ps1          # builds zips + setups into artifacts/
pwsh -File tests/Test-RuntimePackaging.ps1 -ArtifactsDirectory artifacts -AssetsPath src/Flipper.App/obj/project.assets.json
```

Layout:

- `src/Flipper.App` — WinUI 3 frontend
- `src/Flipper.Core` — library, settings, reader logic, updater
- `src/Flipper.Setup` — the self-contained installer exe
- `tests/` — xUnit tests plus a couple of Python tests for the score-audit
  scripts
- [Live wink lab](tools/Flipper.WinkLab/README.md) — collect labelled camera
  attempts and replay them against the wink detector

Each push to `main` runs the tests, builds the four assets, tags
`v<patch-bumped-version>`, and creates a **draft** release. Sign the application
launcher, `Carousel.dll`, `Flipper.Core.dll` and both setup executables with the
release certificate before uploading the final assets. For a local signed build,
`publish.ps1` accepts `-SigningCertificateThumbprint` and `-SignToolPath`.
Run **publish signed release** at the release tag after device checks. It verifies
the signatures, versions, architectures, official ONNX and Visual C++ payloads,
and the wink libraries' native dependencies, attests the
signed assets, then publishes the release. Signing credentials remain local.
The dependency check reads both normal and delayed imports without using the
build machine's installed runtimes. Keep `scripts/vc-runtime.json` current with
Microsoft's servicing releases: app-local DLLs are updated through Carousel
releases. Only release CRT files from the listed packages may be bundled; see
[Microsoft's redistribution list](https://learn.microsoft.com/en-us/visualstudio/releases/2026/redistribution).

## Credits

- PDF rendering: [PDFtoImage](https://github.com/sungaila/PDFtoImage) /
  [PdfPig](https://github.com/UglyToad/PdfPig)
- Wink detection: [YuNet](https://github.com/opencv/opencv_zoo/tree/main/models/face_detection_yunet),
  [Open Model Zoo eye-state model](https://github.com/openvinotoolkit/open_model_zoo/tree/master/models/public/open-closed-eye-0001)
  and [ONNX Runtime](https://github.com/microsoft/onnxruntime) — see
  `src/Flipper.App/Wink/NOTICE.txt`.

## License

MIT — see [LICENSE](LICENSE).
