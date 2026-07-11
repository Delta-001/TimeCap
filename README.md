<p align="center">
  <img src="assets/logo.png" alt="TimeCap — Clip the moment" width="300">
</p>

<h1 align="center">TimeCap</h1>
<p align="center"><em>Clip the moment</em> — instant replay for your screen (Medal.tv / OBS style)</p>

---

TimeCap is a lightweight Windows application that records your screen **continuously** in the background, with no impact on your performance. Whenever something great happens, press a hotkey: the last seconds or minutes are instantly saved as a video clip — *after* the moment happened.

## ✨ Highlights

* **Plug and play** — download one file and run it. Everything else installs itself.
* **Zero impact on your games** — recording runs on your graphics card's dedicated encoder (NVIDIA, AMD and Intel supported), with an automatic software fallback.
* **Instant saving** — clips are assembled in a fraction of a second, with no re-encoding.
* **Multi-monitor** — record the screen you want, or several at once: a multi-screen save creates one folder with one video per display (`Screen1.mp4`, `Screen2.mp4`…).
* **Built-in player & easy sharing** — double-click a clip to watch it inside the app, and use *Copy* (or drag & drop) to paste a clip straight into Discord, WhatsApp or an email.
* **Light and portable** — a single executable. No installer, no .NET runtime, nothing to set up.

## 📥 Download

1. Grab **`TimeCap.exe`** from the **[Releases](../../releases)** page.
2. Run it. On first launch, TimeCap automatically downloads its video engine (FFmpeg, ~30 MB) — this is the only time an internet connection is required.

That's it. No other steps.

> **Windows SmartScreen**: the first time you run TimeCap, Windows may warn that the app is unrecognized ("Windows protected your PC"). This is expected for new applications that are not code-signed yet — click **More info**, then **Run anyway**. The app is open source: you can audit the code in this repository or build it yourself.

## 🖥️ Requirements

* **System**: Windows 10 or Windows 11.
* **Graphics card**: any. TimeCap picks the best encoder available on your machine — NVIDIA (NVENC), AMD (AMF) or Intel (QSV) hardware encoding, with an automatic fallback to software encoding on machines without a supported GPU (works everywhere, uses more CPU).
* **Internet**: only on first launch, for the automatic video engine download.

## 🚀 Getting started

Double-click `TimeCap.exe`:

* A dark window opens with a **REC** indicator (recording is already running) and your recent clips shown as thumbnails. Double-click a clip to play it in the built-in player; multi-screen clips appear as a stacked card that opens right inside the app (with a back button).
* Closing the window keeps TimeCap running in the notification area (next to the Windows clock). Double-click the tray icon to reopen it.

Save a clip anytime with the default hotkeys:

| Hotkey | Saves |
|---|---|
| `Alt + X` | the last 15 seconds |
| `Alt + C` | the last 10 minutes |
| `F11` | the entire available history |

> ⚠️ **Note**: a hotkey without a modifier (like `F11` alone) is captured globally — your games won't receive that key while TimeCap is running. Prefer combinations such as `Alt + F11`.

## ⚙️ Settings

Everything is configurable from the app itself (Settings button), and changes apply immediately — no restart needed:

* Hotkeys and their clip durations (the list is sorted from shortest to longest).
* Which screens to record — one, or several at the same time.
* Framerate, quality, maximum history length, output folder.
* Desktop audio and microphone (recorded on a separate track).

### Sharing a clip

Select a clip and hit **Copy** (or simply drag the card out of the window): the video is placed on the clipboard as a file, ready to paste (`Ctrl+V`) into Discord, WhatsApp, Teams, an email…

**Right-click a clip** for the full toolbox:

* **Get a share link (72 h)** — the clip is uploaded to [Litterbox](https://litterbox.catbox.moe) (catbox.moe's temporary host, up to 1 GB, no account) and the link lands in your clipboard. Perfect for clips too big for Discord.
* **Compress for Discord** — re-encodes under 10 MB and copies the result.
* **Convert to GIF**, **Copy a frame** (PNG to clipboard).
* **Trim in the player** — mark start/end while watching, export the extract instantly (~2 s precision).
* **Pin** (keeps a clip at the top of the gallery), **Rename** (`F2`), **Delete** (`Del`, to the Recycle Bin), **Properties**.

> The built-in player has its own decoding engine (powered by FFmpeg): it plays every TimeCap clip — AV1 included — without needing any codec installed on Windows.

### Advanced: the `config.json` file

A `config.json` file is created automatically in `%APPDATA%\ScreenClipTool` (or next to the executable for portable use). Main options:

| Parameter | Description |
|---|---|
| `output_dir` | Folder where your clips are saved (e.g. `"C:/Clips"`). |
| `max_buffer_minutes` | Length of the rolling history (e.g. `15`). Older footage is deleted automatically so your disk never fills up. |
| `fps` | Recording framerate (`60` or `30`). |
| `audio_enabled` | `true` / `false` — record your PC sound. |
| `mic_enabled` | `true` / `false` — record your microphone on a separate track. |
| `screens` | Displays to record, e.g. `[0]` for the first screen or `[0, 1]` for the first two. Multi-screen saves produce a `Clip_<date>` folder with `Screen1.mp4`, `Screen2.mp4`… |
| `ffmpeg_path` | Optional explicit path to `ffmpeg.exe`. By default TimeCap looks next to the executable, then in `PATH`, then in its own automatic install. |

## 🛠️ For developers

Building from source requires the .NET 8 SDK:

```powershell
# Build in Release mode:
dotnet build -c Release

# Create the single self-contained executable (no .NET needed on the target machine):
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

### Built-in diagnostics

* `TimeCap.exe --selftest result.json` — records ~9 seconds for real, exports a 5-second clip and verifies it (size, duration, streams). Writes a JSON report.
* `TimeCap.exe --uitest` — opens both windows without starting the recorder, to validate the UI.

### Architecture

The capture pipeline (ddagrab → NVENC → 2-second segments → stream-copy concat), the robustness behaviors (auto-restart, orphan-process protection, audio keepalive) and the known limitations are documented in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) (French).

Releases are built automatically by GitHub Actions when a `v*` tag is pushed.
