# What's new in Savor

## 0.6.7 · Oct 29, 2026
- Much lower memory when recording with the CPU encoder (x264): about 140 MB instead of several GB on high-core CPUs at 1440p.
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
