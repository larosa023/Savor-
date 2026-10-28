<p align="center"><img src="docs/icon.png" width="96" alt="Savor icon"></p>

<h1 align="center">Savor</h1>

<p align="center">A lightweight, modern screen recorder for Windows with an instant replay buffer.<br>
Like OBS, minus the streaming, scenes and clutter.</p>

---

## Features

- **Replay buffer** — Savor quietly keeps the last 10 seconds to 10 minutes of your screen. Press **Alt + F10** and that moment is saved as a clip.
- **Recording** — classic start/stop recording with **Alt + F9**. It shares the encoder with the replay buffer, so running both costs nothing extra.
- **GPU encoding** — NVIDIA NVENC (H.264 / HEVC / AV1), AMD AMF and Intel Quick Sync, detected automatically. Falls back to x264 on the CPU.
- **Low-overhead capture** — Windows Desktop Duplication straight into the hardware encoder, no frame copies on supported GPUs.
- **Audio** — desktop audio plus an optional microphone, mixed into one track with live level meters and per-source volume.
- **Global hotkeys** that work while you're in a game, and a tray icon so Savor stays out of the way.
- **Recent captures** gallery with thumbnails — click to play, right-click to copy the file (paste straight into Discord), show in folder, or delete.
- Quiet notifications that never steal focus from your game.
- MP4 or MKV output, launch-at-startup, monitor picker, 30–240 fps.

## Download

Grab `Savor-win-x64.zip` from the [latest release](../../releases/latest), unzip it anywhere and run **Savor.exe**.
No installer and no .NET install needed — FFmpeg is bundled next to the exe.

Captures go to `Videos\Savor` by default (change it in Settings).

## Default hotkeys

| Action | Shortcut |
| --- | --- |
| Save clip (last N seconds) | Alt + F10 |
| Start / stop recording | Alt + F9 |
| Replay buffer on / off | Alt + F8 |

All of them can be changed in Settings → Hotkeys.

## How it works

Savor runs a single FFmpeg process that captures the display with `ddagrab` (Desktop Duplication) and encodes it into 2-second MPEG-TS segments in `%LOCALAPPDATA%\Savor\buffer`.

- The **replay buffer** keeps only the newest segments. Saving a clip stitches the last N seconds together with a lossless stream copy, so it's near-instant and never re-encodes.
- **Recording** pins every segment from the moment you pressed record and stitches them into one file when you stop. If capture ever crashes mid-recording, Savor salvages what was recorded.

Audio is captured with WASAPI (loopback for desktop audio, plus your mic), mixed in Savor, and piped into FFmpeg.

## Building from source

Requires the .NET 8 SDK on Windows.

```powershell
dotnet publish src/Savor/Savor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

Put an `ffmpeg.exe` (a recent build with `ddagrab`, e.g. from [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds/releases)) next to the built `Savor.exe`.
Every push to `main` is built by GitHub Actions; pushing a `v*` tag publishes a release.

## Notes

- Windows 10 (1903+) or Windows 11.
- A few games in true exclusive fullscreen can't be captured by Desktop Duplication — borderless/windowed fullscreen always works.
- Capture pauses on secure screens (UAC prompts, Ctrl+Alt+Del); turn the buffer back on afterwards.

FFmpeg is licensed separately under the GPL; its license is included in the release zip as `FFMPEG-LICENSE.txt`.
