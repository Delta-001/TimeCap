<p align="center">
  <img src="assets/logo.png" alt="TimeCap — Clip the moment" width="300">
</p>

<h1 align="center">TimeCap</h1>
<p align="center"><em>Clip the moment</em> — instant replay for your screen (Medal.tv / OBS style)</p>

---

TimeCap is a lightweight Windows application that records your screen **continuously** in the background, with no impact on your performance. Whenever something great happens, press a hotkey: the last seconds or minutes are instantly saved as a video clip — *after* the moment happened.

## ✨ Highlights

* **Plug and play** — download one file and run it. Everything else installs itself.
* **Zero impact on your games** — recording runs 100% on your NVIDIA graphics card.
* **Instant saving** — clips are assembled in a fraction of a second, with no re-encoding.
* **Light and portable** — a single executable. No installer, no .NET runtime, nothing to set up.

## 📥 Download

1. Grab **`TimeCap.exe`** from the **[Releases](../../releases)** page.
2. Run it. On first launch, TimeCap automatically downloads its video engine (FFmpeg, ~30 MB) — this is the only time an internet connection is required.

That's it. No other steps.

## 🖥️ Requirements

* **System**: Windows 10 or Windows 11.
* **Graphics card**: NVIDIA GeForce (RTX 40/50 series recommended for AV1; older cards automatically fall back to HEVC).
* **Internet**: only on first launch, for the automatic video engine download.

## 🚀 Getting started

Double-click `TimeCap.exe`:

* A dark window opens with a **REC** indicator (recording is already running) and your recent clips shown as thumbnails.
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
* Framerate, quality, maximum history length, output folder.
* Desktop audio and microphone (recorded on a separate track).

### Advanced: the `config.json` file

A `config.json` file is created automatically in `%APPDATA%\ScreenClipTool` (or next to the executable for portable use). Main options:

| Parameter | Description |
|---|---|
| `output_dir` | Folder where your clips are saved (e.g. `"C:/Clips"`). |
| `max_buffer_minutes` | Length of the rolling history (e.g. `15`). Older footage is deleted automatically so your disk never fills up. |
| `fps` | Recording framerate (`60` or `30`). |
| `audio_enabled` | `true` / `false` — record your PC sound. |
| `mic_enabled` | `true` / `false` — record your microphone on a separate track. |
| `output_idx` | Which display to record: `0` for the first screen, `1` for the second, etc. |
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
