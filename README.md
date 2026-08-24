# Carousel

Carousel is a Windows app for reading sheet music off a screen without touching
anything. You point it at a folder of PDFs, it builds a library, and you turn
pages by voice — say *flip*, *next page*, *back* — while your hands are busy
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
- **Voice** — always-on keyword spotting (sherpa-onnx zipformer model bundled,
  runs locally, nothing leaves the machine). Commands: flip, turn page, next,
  back, previous, restart, beginning, finish.
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

```text
dotnet test Flipper.sln -c Release
dotnet run --project src/Flipper.App/Flipper.App.csproj
pwsh -File scripts/publish.ps1          # builds zips + setups into artifacts/
```

Layout:

- `src/Flipper.App` — WinUI 3 frontend
- `src/Flipper.Core` — library, settings, reader logic, updater
- `src/Flipper.Setup` — the self-contained installer exe
- `tests/` — xUnit tests plus a couple of Python tests for the score-audit
  scripts

Releases are automatic: each push to `main` runs the tests, publishes, tags
`v<patch-bumped-version>`, and cuts a release with all four assets attached.

## Credits

- Keyword spotting: [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx)
  (`kws-zipformer-zh-en-3M-2025-12-20`, int8 encoder/joiner) — see
  `src/Flipper.App/Voice/NOTICE.txt`
- PDF rendering: [PDFtoImage](https://github.com/sungaila/PDFtoImage) /
  [PdfPig](https://github.com/UglyToad/PdfPig)

## License

MIT — see [LICENSE](LICENSE).
