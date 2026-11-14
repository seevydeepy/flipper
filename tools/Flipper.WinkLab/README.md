# Live wink lab

A local Windows app to collect labelled wink and blink attempts for Flipper.
It uses the reader's camera service, eye estimator, models and page-turn detector.
Lab attempts use the default eye thresholds, not the reader's saved personal
calibration profile. Test a saved profile in Carousel's calibration window and reader.
The preview is mirrored. Eye names refer to your own left and right eyes.

## Collect attempts

1. Start the app and select **Start camera**.
2. Select **Record attempt** or press Space. Keep both eyes open during the
   one-second lead-in.
3. When the prompt changes, make one right wink, one left wink, one blink, or
   no gesture. Reopen both eyes before the three-second period ends.
4. Select what you actually did. The app compares that label with the detected
   turns, shows the result, and saves the attempt. Use **Discard attempt** if
   you made more than one gesture or are unsure of the label.
5. Repeat with a mixture of gestures, including ordinary blinks and no gesture.

Right wink means one forward turn. Left wink means one backward turn. A blink
or no gesture means no turn. Results distinguish correct, missed, wrong
direction, extra turn and unwanted turn.

Only labelled attempts are saved. Live preview frames outside an attempt are
not saved. Closing the app drops an unlabelled attempt and waits for any save
already in progress. There is no audio capture or upload.

Each session has a new folder under `%LOCALAPPDATA%\Flipper\WinkLab`, shown in
the app. `--output SESSION_FOLDER` selects a different, empty folder. Each
`trial-0001.zip` contains the exact processed BGRA frames, capture timestamps,
original eye probabilities and turn decisions, plus your label. These are
replay archives, not a standard video format. Keep them outside the repository.

## Build and replay

From the repository root:

```powershell
dotnet run --project tools/Flipper.WinkLab -c Release
dotnet publish tools/Flipper.WinkLab -c Release -r win-x64 --self-contained true -o artifacts/wink-lab
```

Replay a session folder or one trial with a build of the current detector:

```powershell
dotnet artifacts/wink-lab/Flipper.WinkLab.dll replay C:\path\to\session C:\path\to\report.json
```

The JSON report contains the original and replayed outcome for every attempt,
with per-frame eye states, probabilities and turns. The replay feeds the same
frames and timestamps into a fresh detector for each attempt, including the
lead-in. It does not use the recorded decisions to produce the replay result.
An existing report or trial is never overwritten.

Every fifth saved attempt is assigned to validation; the others are assigned
to tuning. Use the tuning attempts to choose thresholds, then assess the
reserved validation attempts without choosing thresholds from their results.
Repeat with a fresh session to check that any improvement holds up. Lab
attempts reset detector state; also check continuous use in the reader after
tuning.
