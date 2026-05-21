# TapeDeck

TapeDeck is a small Windows-only .NET recorder. It records the selected or default Windows playback/render endpoint, the selected or default microphone/capture endpoint, and writes one replayable mixed audio file. It includes both a CLI and a tiny Windows desktop app.

This project targets `net8.0-windows` because .NET 10 was not available in this environment. It uses NAudio `2.3.0` and does not require admin rights.

## What it records

- Default playback device/system audio by default.
- Default microphone by default.
- One selected playback/render device when `--system-device` is supplied.
- One selected microphone/capture device when `--mic-device` is supplied.
- A final mixed M4A file by default: 48,000 Hz, stereo, AAC, 128 kbps.
- An optional uncompressed WAV file when `--format wav` is supplied: 48,000 Hz, stereo, 16-bit PCM.

## What it does not do

- Does not record video.
- Does not record every Windows output device at once unless a specific playback endpoint is selected for the recording.
- Does not record app-specific audio separately.
- Does not write MP3.
- Does not use OBS, ffmpeg, Audacity, LAME, external executables, native DLL extraction, telemetry, registry persistence, autostart, a tray app, or streaming.

## Project layout

```text
src/TapeDeck.Core   Shared recording, device, naming, and mixdown code.
src/TapeDeck.Cli    Command-line app. The executable name remains TapeDeck.
src/TapeDeck.App    Tiny Windows Forms app.
tests/TapeDeck.Tests
```

## Usage

```powershell
TapeDeck record
TapeDeck record --out "C:\Recordings\meeting.m4a"
TapeDeck record --duration 01:00:00
TapeDeck devices
TapeDeck record --system-device "<id>" --mic-device "<id>"
TapeDeck record --no-mic
TapeDeck record --no-system
TapeDeck record --format wav --out "C:\Recordings\meeting.wav"
```

Default recordings are written to:

```text
%USERPROFILE%\Recordings\TapeDeck\yyyy-MM-dd_HH-mm-ss.m4a
```

Use `TapeDeck devices` to list playback/render and microphone/capture endpoints. Device selection accepts either the stable device ID or the exact friendly name.

## Tiny Windows app

Run the app with:

```powershell
dotnet run --project src/TapeDeck.App
```

The app records the default system audio and microphone by default. It lets you pick M4A or WAV, start recording, stop recording, and then choose the final save location using the standard Windows save dialog. If you cancel the save dialog, the completed recording is left in the temp folder and the app shows that path.

## Options

```text
TapeDeck record --out "C:\Recordings\meeting.m4a"
TapeDeck record --format m4a
TapeDeck record --format wav
TapeDeck record --bitrate 128000
TapeDeck record --no-mic
TapeDeck record --no-system
TapeDeck record --system-device "<id-or-exact-name>"
TapeDeck record --mic-device "<id-or-exact-name>"
TapeDeck record --duration 01:30:00
TapeDeck record --split-minutes 60
TapeDeck record --keep-stems
TapeDeck record --overwrite
TapeDeck record --system-gain 1.0
TapeDeck record --mic-gain 1.0
```

`--format` accepts `m4a`, `mp4a`, `aac`, `wav`, or `wave`. M4A is the default. If `--format` is not supplied, TapeDeck also infers the format from a `.m4a` or `.wav` `--out` extension.

When `--split-minutes 60` is used with `meeting.m4a`, final files are written as:

```text
meeting.part001.m4a
meeting.part002.m4a
```

When `--keep-stems` is supplied, TapeDeck keeps finalized source stems beside the output:

```text
meeting.system.wav
meeting.mic.wav
```

If final mixing fails, TapeDeck preserves the source stems and prints their paths.

## Notes

Headphones are recommended. Recording microphone audio while speaker playback is audible can cause echo or feedback in the final file.

M4A files are much smaller than WAV files. The default 128 kbps AAC output is about 58 MB per hour, while 48 kHz stereo 16-bit PCM WAV is about 691 MB per hour. WAV output remains available for compatibility and debugging, and TapeDeck guards WAV final files at approximately 3.5 GB; use `--split-minutes` for long WAV recordings.

Check meeting consent requirements and your company policy before recording calls or meetings.

## Build and run

```powershell
dotnet build TapeDeck.sln
dotnet run --project src/TapeDeck.Cli -- devices
dotnet run --project src/TapeDeck.Cli -- record
dotnet run --project src/TapeDeck.App
```

## Test

```powershell
dotnet test tests/TapeDeck.Tests/TapeDeck.Tests.csproj
```

The unit tests do not require live audio devices.

## Publish single Windows executables

```powershell
dotnet publish src/TapeDeck.Cli/TapeDeck.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
dotnet publish src/TapeDeck.App/TapeDeck.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Automated releases

Pushes to `main` run `.github/workflows/release.yml`. The workflow restores the pinned local `GitVersion.Tool` from `.config/dotnet-tools.json`, computes the release version from Git history using `GitVersion.yml`, builds and tests the solution, publishes the CLI and Windows app as self-contained `win-x64` outputs, zips both publish folders, and creates a GitHub Release named `v<version>`.

Versioning uses GitVersion's GitHubFlow workflow with `v` tags and patch increments on `main`. In a repository with no SemVer tags, GitVersion starts from its default initial version; each new mainline change after a release tag increments from the previous tag unless a commit message uses GitVersion's semver bump markers.
