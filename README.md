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
- **Audio** — desktop audio plus an optional microphone, with live level meters, volume and mute per source.
- **Multiple audio tracks** — track 1 is the full mix (what players and Discord play), then desktop audio, your mic, and optional per-app tracks. Turn it off in Settings → Audio for a single track.
- **App audio tracks** — give up to 5 apps (your game, Discord, Spotify…) their own track, like OBS's Application Audio Capture. Apps opened later are picked up automatically. Leave the list empty to keep just desktop audio. Needs Windows 10 2004 or newer.
- **Lightweight** — about 50 MB of memory with the window open and about 25 MB in the tray (plus the FFmpeg capture process).
- **Global hotkeys** that work while you're in a game, and a tray icon so Savor stays out of the way.
- **Captures library** with thumbnails — clips are named after the game you're playing and can be filtered by game. Right-click to rename, copy the file (paste straight into Discord), show in folder, or delete.
- **Built-in viewer & editor** — click any capture to play it inside Savor, trim it (drag handles or I / O), crop it (free, 16:9, 9:16 for Shorts/TikTok, 1:1, 4:3), choose which audio tracks to keep, and export with your GPU encoder. Fast export does lossless trims instantly. Shortcuts: Space play/pause, ← / → step, Shift + ← / → 5 s, Esc back.
- **Storage limit & low-disk protection** — keep captures under a size you choose; Savor warns when the drive is nearly full and stops a recording safely before it fills up.
- **What's new** — the clock icon in the top bar lists every update.
- Quiet notifications that never steal focus from your game, plus an optional save chime.
- MP4 or MKV output, launch-at-startup, monitor picker, 30–240 fps, and an output resolution option (record a 1440p screen at 1080p or 720p).
- **Copy diagnostics** in Settings → About, for quick troubleshooting.
- **Library tools** — search, multi-select (Ctrl/Shift + click) with bulk game changes, join, copy and delete, drag captures straight into Discord or a folder, and hover previews.
- **Editor extras** — export as GIF, change speed (0.25×–2×).
- **Mic noise suppression** — RNNoise removes keyboard clicks, fans and hum from your mic (model from GregorR/rnnoise-models).

## Download

Download **Savor.exe** from the [latest release](../../releases/latest) and run it. That's it — one file, no installer, nothing else to install.
On first launch Savor unpacks its built-in FFmpeg to `%LOCALAPPDATA%\Savor\ffmpeg` (a second or two, once).

Captures go to `Videos\Savor` by default (change it in Settings).

## Default hotkeys

| Action | Shortcut |
| --- | --- |
| Save clip (last N seconds) | Alt + F10 |
| Start / stop recording | Alt + F9 |
| Replay buffer on / off | Alt + F8 |

All of them can be changed in Settings → Hotkeys — any key works (Delete, Insert, a lone Ctrl, F-keys, numpad), with or without modifiers, plus mouse side buttons and middle click. Hotkeys never block the key in other apps.

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

To embed FFmpeg like the release does, gzip a recent `ffmpeg.exe` (with `ddagrab`, e.g. from [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds/releases)) to `ffmpeg/ffmpeg.exe.gz` before publishing. Without it, Savor uses an `ffmpeg.exe` next to the exe or on PATH.
Every push to `main` is built by GitHub Actions and published as the release for the version in `Savor.csproj` (bump `<Version>` for a new release).

## Notes

- Windows 10 (1903+) or Windows 11.
- A few games in true exclusive fullscreen can't be captured by Desktop Duplication — borderless/windowed fullscreen always works.
- Capture pauses on secure screens (UAC prompts, Ctrl+Alt+Del); turn the buffer back on afterwards.

FFmpeg is licensed separately under the GPL; Savor embeds an unmodified [BtbN](https://github.com/BtbN/FFmpeg-Builds) build.
