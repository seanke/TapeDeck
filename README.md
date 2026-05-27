# TapeDeck

TapeDeck is a small Windows-only recorder for capturing system playback audio, microphone audio, or both. It writes one mixed replayable audio file and is available as both a command-line tool and a tiny Windows desktop app.

TapeDeck targets `net8.0-windows`, uses NAudio `2.3.0`, and does not require admin rights.

## Features

- Records the default Windows playback/render device by default.
- Records the default Windows microphone/capture device by default.
- Can target specific playback or microphone devices by ID or exact friendly name.
- Writes M4A/AAC by default: 48,000 Hz, stereo, 128 kbps.
- Can write uncompressed WAV with `--format wav`: 48,000 Hz, stereo, 16-bit PCM.
- Can write a local TXT transcript with `--transcript`.
- Preserves quiet gaps in the recording timeline.
- Keeps memory usage stable by recording and mixing in blocks.
- Includes optional WAV stems for troubleshooting with `--keep-stems`.

## Non-Goals

TapeDeck does not record video, record every Windows output device at once, record app-specific audio separately, write MP3, stream audio, run as a tray app, autostart, write telemetry, persist registry settings, call external executables, or extract native DLLs.

## CLI Usage

```powershell
TapeDeck record
TapeDeck record --out "C:\Recordings\meeting.m4a"
TapeDeck record --duration 01:00:00
TapeDeck record --format wav --out "C:\Recordings\meeting.wav"
TapeDeck record --transcript
TapeDeck record --system-device "<id>" --mic-device "<id>"
TapeDeck record --no-mic
TapeDeck record --no-system
TapeDeck devices
```

Default recordings are written to:

```text
%USERPROFILE%\Recordings\TapeDeck\yyyy-MM-dd_HH-mm-ss.m4a
```

Use `TapeDeck devices` to list playback/render and microphone/capture devices. Device selection accepts either the stable device ID or the exact friendly name.

## CLI Options

```text
TapeDeck record --format m4a
TapeDeck record --format wav
TapeDeck record --bitrate 128000
TapeDeck record --duration 01:30:00
TapeDeck record --split-minutes 60
TapeDeck record --keep-stems
TapeDeck record --overwrite
TapeDeck record --system-gain 1.0
TapeDeck record --mic-gain 1.0
TapeDeck record --system-device "<id-or-exact-name>"
TapeDeck record --mic-device "<id-or-exact-name>"
TapeDeck record --transcript
TapeDeck record --transcript-out "C:\Recordings\meeting.txt"
TapeDeck record --transcript-culture en-US
```

`--format` accepts `m4a`, `mp4a`, `aac`, `wav`, or `wave`. If `--format` is omitted, TapeDeck infers the format from a `.m4a` or `.wav` output path when possible.

`--transcript` creates a TXT sidecar beside the audio file, for example `meeting.txt`. It uses installed Windows speech recognition locally through `System.Speech`; it does not call an online transcription service. Accuracy depends on the installed Windows speech recognizer and language pack. Use `--transcript-culture` to choose a specific installed recognizer culture.

`--transcript` cannot currently be combined with `--split-minutes`.

When `--split-minutes 60` is used with `meeting.m4a`, final files are written as:

```text
meeting.part001.m4a
meeting.part002.m4a
```

When `--keep-stems` is supplied, TapeDeck keeps the finalized source WAV files beside the output:

```text
meeting.system.wav
meeting.mic.wav
```

If final mixing fails, TapeDeck preserves the source stems and prints their paths.

## Desktop App

Run the app with:

```powershell
dotnet run --project src/TapeDeck.App
```

The app records the default system audio and microphone, lets you choose M4A or WAV, optionally creates a local TXT transcript, and prompts for the save location when recording stops.

## Practical Notes

Headphones are recommended. Recording microphone audio while speaker playback is audible can cause echo or feedback in the final file.

M4A files are much smaller than WAV files. The default 128 kbps AAC output is about 58 MB per hour. A 48 kHz stereo 16-bit PCM WAV file is about 691 MB per hour. TapeDeck guards final WAV files at approximately 3.5 GB; use `--split-minutes` for long WAV recordings.

Check meeting consent requirements and company policy before recording calls or meetings.

## Project Layout

```text
src/TapeDeck.Core   Shared recording, device, naming, and mixdown code.
src/TapeDeck.Cli    Command-line app. The executable name is TapeDeck.
src/TapeDeck.App    Tiny Windows Forms app.
tests/TapeDeck.Tests
```

## Build and Test

```powershell
dotnet build TapeDeck.sln
dotnet test tests/TapeDeck.Tests/TapeDeck.Tests.csproj
dotnet run --project src/TapeDeck.Cli -- devices
dotnet run --project src/TapeDeck.Cli -- record
dotnet run --project src/TapeDeck.App
```

The unit tests do not require live audio devices.

## Publish

```powershell
dotnet publish src/TapeDeck.Cli/TapeDeck.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
dotnet publish src/TapeDeck.App/TapeDeck.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Automated Releases

Pushes to `main` run `.github/workflows/release.yml`. The workflow computes the version with GitVersion, builds and tests the solution, publishes the CLI and desktop app as self-contained `win-x64` outputs, zips both publish folders, and creates a GitHub Release named `v<version>`.

Versioning uses GitVersion's GitHubFlow workflow with `v` tags and patch increments on `main`.
