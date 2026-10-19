# ReelGood

ReelGood is a native Windows video review and editing prototype. It uses a familiar editor workspace: a preview, multi-track timeline, audio waveform rows, cue review controls, and a trim range.

## Run the standalone Windows app

Open `release\ReelGood.exe` and keep its `tools` folder beside it. The `release` folder is self-contained and does not need the .NET desktop runtime installed separately.

## Build from source

1. Run `powershell -ExecutionPolicy Bypass -File tools\Install-FFmpeg.ps1` to download FFmpeg 9.0.2 Essentials and verify its SHA-256 before extracting the binaries.
2. Open `ReelGood.csproj` with Visual Studio or build it with the .NET 9 SDK on Windows.
3. For a self-contained release, publish for `win-x64` with self-contained deployment enabled.

FFmpeg executables and build output are intentionally excluded from Git history. The setup script restores the local FFmpeg tools.

## Current features

- Import and preview video files.
- Inspect separate audio streams, extract each as WAV, and display separate waveform rows.
- Scan for audio-level changes as review suggestions, then jump to, keep, or skip each cue.
- Record the desktop system-audio mix and microphone as separate streams.
- Trim a clip and export it while preserving source audio streams.
- Store cue feedback and approved preference notes on this Windows account.

## Limits

Audio-level changes are only candidate cues; the app does not yet detect jokes or laughter with a trained semantic comedy model. Desktop loopback records the Windows system mix, so it cannot reliably isolate Xbox party chat from game audio when Windows presents them as one mixed device. Recording availability and quality depend on the selected Windows input devices and permissions.

Preference notes are saved for future behavior, but this build does not train or update model weights. Recommendations remain reviewable and require user action.

## Third-party media tools

The bundled FFmpeg 9.0.2 Essentials build is from gyan.dev and is licensed under GPLv3. See `tools\FFmpeg-GPLv3-LICENSE.txt` and `tools\FFmpeg-build-notes.txt` for license and build information. The installer script verifies archive SHA-256 `60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba` before extraction.
