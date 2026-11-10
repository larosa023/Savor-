# What's new in Savor

## 0.9.13 · Nov 10, 2026
- New: dropped-frame warning. Savor checks each part of the replay buffer as it's written (by frame sizes only, so it costs almost nothing) and notifies you if capture keeps missing screen frames, with the most likely fix for your PC, so you find out before saving a choppy clip. Turn it off in Settings → Capture → Warn about dropped frames.
- Copy diagnostics now includes how many short freezes capture had in the last 30 seconds.

## 0.9.12 · Nov 8, 2026
- Fixed choppy clips (freezing for a split second every half second), even with no game running. Since the audio sync fix, FFmpeg kept pausing the screen capture to wait for the audio and dropped screen frames. Screen capture now runs in its own process that nothing can pause, and Savor passes its video on to be combined with the audio, so every frame is kept. Audio sync works exactly as before.
- Any previous "Check audio sync" result is reset, since it was measured with the old timing. Run it again if sound ever seems off.

## 0.9.11 · Nov 8, 2026
- Settings → Capture priority now shows the GPU priority Windows actually gave the capture (realtime, high or normal), not just whether Savor runs as administrator.

## 0.9.10 · Nov 8, 2026
- Fixed high GPU priority not taking effect on some PCs (Windows said "invalid parameter"): Savor now applies it once the capture engine is using the GPU, so screen capture keeps getting frames when a game maxes out the GPU.
- Settings now warns when the CPU encoder is selected even though a GPU encoder works.

## 0.9.9 · Nov 8, 2026
- Smoother clips in games that max out your GPU: Savor now gives its capture engine high GPU priority, so Windows doesn't push screen capture behind the game (like OBS does).
- New: Restart as administrator (Settings → Capture → Capture priority) unlocks realtime GPU priority, the smoothest option for GPU-heavy games.

## 0.9.9 · Nov 8, 2026
- Works with Isle, the Dynamic Island for Windows: while Isle is running, a live Recording card with a timer, Stop and Marker sits at the top of your screen, saved clips pop up there with a thumbnail and Edit / Copy / Show in folder, and Savor's notifications show there instead of in the corner. Turn it off in Settings → General → Show on Isle.

## 0.9.7 · Nov 8, 2026
- Check audio sync (Settings → Audio): Savor flashes a square and plays a few beeps while recording, measures the gap between sound and picture on your PC, and corrects it for every new clip. Run it once; run it again to confirm.
- More headroom for audio timing on busy PCs, so sound stays in sync even under heavy game load.
- The editor's preview now keeps sound and picture within about 40 ms (it could drift up to 120 ms before), so nudging Audio sync by ear is accurate.

## 0.9.6 · Nov 7, 2026
- Fixed audio being late in captures (around 0.1–0.2 s). Windows hands Savor recorded audio in batches, a bit after it was heard; Savor now times every piece of audio by when it was heard, on the same clock as the screen.
- Audio sync fix for clips you've already saved: in the editor, nudge the sound earlier or later under Audio sync (Shift for bigger steps), hear it right away, then Export a fixed copy.
- New capture method option (Settings → Capture): Windows Graphics Capture. Use it if videos from streaming apps or a browser look choppy in your clips — Desktop Duplication (still the default, best for games) can miss frames from video players. If it doesn't work on your PC, Savor switches back by itself.

## 0.9.5 · Nov 7, 2026
- Fixed the capture engine growing to several GB of memory on some NVIDIA cards (seen on a GTX 1080 Ti). If direct GPU capture starts piling up memory, Savor switches that encoder to compatibility capture (frames go through system memory first, using a little CPU) and remembers it, so it stays low from then on.
- Copy diagnostics now says Windows 11 correctly (Windows reports itself as "Windows 10" internally).

## 0.9.4 · Nov 7, 2026
- Memory safety net: if Savor's capture engine (ffmpeg) ever uses more than 2 GB of memory, Savor restarts it automatically — your replay buffer and recording are kept — and lets you know once. The live preview has the same guard.
- Removed a capture setting that could make the capture engine hold video in memory while waiting for audio.
- Copy diagnostics now includes the capture engine's memory use and more of its recent log.

## 0.9.3 · Nov 5, 2026
- Fixed audio being out of sync with the video. Audio that piled up while capture was starting made the whole soundtrack late (about a third of a second), and long sessions could slowly drift. Audio is now timed against the same clock as the screen and kept locked to it. Clips saved before this update keep their offset.

## 0.9.2 · Nov 5, 2026
- Merge clips, then trim: select two or more captures and press Merge (formerly Join). The merged video opens straight in the editor, with a yellow marker where each clip starts so the seams are easy to find. Each clip's own markers come along too.

## 0.9.1 · Nov 3, 2026
- Markers: press Alt + F7 in-game to mark a moment (change it in Settings → Hotkeys). Markers show as yellow ticks on the editor's timeline — click near one to jump to it, or press [ and ] to go to the previous / next marker. They stay with the clip through trims, speed changes, renames and game changes.
- “Clip saved” notifications now have an Edit button that opens the clip straight in the editor. Hovering the notification keeps it on screen.

## 0.8.3 · Nov 3, 2026
- Smooth trimming right after opening a clip: preview frames for the whole clip are ready almost instantly (detail fills in a few seconds later), so the picture no longer falls behind on long or 1440p clips.

## 0.8.1 · Nov 3, 2026
- Opens faster: Savor now ships precompiled, so it starts and responds to its first clicks quicker.
- Captures stay smooth with big libraries: searching and switching games no longer re-read the folder, the grid only redraws when something changed, and nothing is scanned while Savor is in the tray.
- Renaming and deleting no longer freeze the window, even when a file is busy.
- Fixed: games with characters like “:” in their name now rename their captures properly.
- Fixed: joining clips made with different encoders could produce a broken video — Savor now checks and re-encodes when needed.
- Fixed: a speed-changed export could replace the original file; it now always saves a new file.
- Fixed: leaving the editor during an export, changing resolution and leaving Settings right away, and a few other rare edge cases.

## 0.8.0 · Oct 31, 2026
- Drag captures straight out of Savor into Discord, a browser or a folder.
- Search captures by name or game (top of Captures).
- Select several captures with Ctrl or Shift + click (Ctrl + A for all), then move them to a game, join, copy or delete them together.
- Hover a capture to see a quick preview of what's in it.
- Editor: export as a GIF, and change the speed (0.25× to 2×).
- Join clips: select two or more and press Join to make one video, oldest first.
- Mic noise suppression (Settings → Audio): removes keyboard clicks, fans and background hum from your mic.

## 0.7.4 · Oct 31, 2026
- Change a clip's game: right-click a capture → Game, then pick one of your games, “New game…”, or “No game”.

## 0.7.3 · Oct 31, 2026
- Fixed an error popup when renaming a game category.

## 0.7.2 · Oct 31, 2026
- Rename game / app categories: right-click a category at the top of Captures → Rename. Its captures move to the new name, and new captures from that app use it too.

## 0.7.1 · Oct 30, 2026
- Clips and recordings are named after the game you're playing, like “VALORANT 2026-10-30 21.14.05”.
- Filter your captures by game: chips at the top of Captures show each game and how many clips it has.
- Rename captures: right-click → Rename, or click the clip's name in the viewer and type.
- Resolution setting (Settings → Capture): record a big screen at 1440p, 1080p or 720p for smaller files and less load on your PC.
- Copy diagnostics (Settings → About): copies your setup and recent errors in one click, to paste to whoever is helping.

## 0.6.8 · Oct 29, 2026
- NVIDIA GPU recording works again on GTX 10-series cards (1060, 1070, 1080 Ti…) and other cards on older drivers. Savor no longer needs NVIDIA driver 610 or newer.

## 0.6.7 · Oct 29, 2026
- Much lower memory when recording with the CPU encoder (x264): about 250 MB instead of several GB on high-core CPUs at 1440p, with the same file size.
- If no GPU encoder works, Settings → Encoder now says why (usually an outdated graphics driver) and which graphics card was found.
- Encoders are re-detected automatically after a graphics driver update.

## 0.6.6 · Oct 29, 2026
- Volume slider for the save sound (Settings → General). It plays a preview as you adjust it.

## 0.6.5 · Oct 29, 2026
- Fixed hotkeys sometimes stopping working while Savor is in the tray. Hotkeys now run on their own and re-attach automatically.
- Capture now reconnects by itself after a game changes resolution, a lock screen or UAC prompt, sleep, or switching audio devices (like plugging in headphones). The replay buffer and recordings carry on instead of turning off.
- If Savor is ever force-closed, its capture engine now always closes with it.
- Lighter when idle: Savor no longer redraws its window while it's hidden, and checks in far less often from the tray.

## 0.6.4 · Oct 29, 2026
- Slightly smaller recording dot on the taskbar and tray icons.

## 0.6.3 · Oct 29, 2026
- A red dot on Savor's taskbar and tray icons shows when the replay buffer or a recording is on.

## 0.6.2 · Oct 29, 2026
- Removed the Discord-size export option from the editor.

## 0.6.1 · Oct 29, 2026
- "What's new" only marks updates you haven't seen yet.

## 0.6.0 · Oct 29, 2026
- Storage limit: keep your captures under a size you choose (Settings → Output). The oldest are removed automatically.
- Low disk protection: Savor warns you when your drive is getting full, and stops a recording safely (keeping what was recorded) before it fills up.
- Optional save sound: a short, quiet chime when a clip or recording is saved (Settings → General, off by default).
- Discord-size export: in the editor, pick "Fit 10 MB", "50 MB" or "500 MB". Savor sizes the video to fit and copies it, ready to paste into Discord.
- This "What's new" panel.

## 0.5.3 · Oct 28, 2026
- Clips with separate audio tracks now play with sound in the viewer.
- "Listen to": switch each audio track on or off in the viewer (Mix, Desktop, Mic, apps).
- New export option: "Tracks I'm listening to".

## 0.5.1 · Oct 28, 2026
- App audio tracks: give apps like your game, Discord or Spotify their own audio track (up to 5).
- Each app gets its own meter, volume and mute in the mixer. Apps opened later are picked up automatically.

## 0.4.10 · Oct 28, 2026
- Much sharper preview frames while trimming, and pausing on a spot shows the exact full-quality frame.

## 0.4.8 · Oct 28, 2026
- Fixed washed-out, too-bright colors when watching captures in the viewer.

## 0.4.7 · Oct 28, 2026
- Lag-free trimming: dragging the timeline shows instant preview frames instead of making the video seek.

## 0.4.5 · Oct 28, 2026
- Fixed an error when opening dropdowns in the editor.
- Bigger, easier-to-grab trim handles and a smoother timeline.
- Keyboard shortcuts keep working after using a dropdown.

## 0.4.3 · Oct 28, 2026
- Built-in viewer & editor: watch clips in Savor, trim, crop (16:9, 9:16, 1:1, 4:3 or free), choose audio tracks and export.

## 0.3.0 · Oct 28, 2026
- Multiple audio tracks: mix, desktop audio and microphone as separate tracks.
- Much lighter: about 50 MB of memory with the window open and 25 MB in the tray.

## 0.2.3 · Oct 27, 2026
- Hotkeys can be any key (Delete, Insert, a lone Ctrl…) or mouse side buttons, and never block the key in other apps.

## 0.2.2 · Oct 27, 2026
- Smoother audio meters.

## 0.2.1 · Oct 27, 2026
- Smoother live preview at 60 fps.

## 0.2.0 · Oct 27, 2026
- New Studio view: live preview of what's being captured, audio mixer with meters, and a sources panel.
- Captures moved to their own tab.

## 0.1.3 · Oct 27, 2026
- In-app updates: an Update button appears when a new version is out.
- Fixed capture failing to start.

## 0.1.1 · Oct 27, 2026
- Savor is now a single .exe, with nothing else to download.

## 0.1.0 · Oct 27, 2026
- First release: replay buffer, recording, GPU encoding, desktop and mic audio, hotkeys and a tray icon.
