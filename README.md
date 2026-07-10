<p align="center">
  <img src="assets/logo.png" alt="TimeCap — Clip the moment" width="300">
</p>

<h1 align="center">TimeCap</h1>
<p align="center"><em>Clip the moment</em> — Lightweight background replay buffer (Medal.tv / OBS style)</p>

---

TimeCap (`ScreenClipTool.exe` executable) is a lightweight Windows application that runs in the background (in your system tray). It records your screen **continuously** without impacting your performance. As soon as you hit a hotkey, it instantly saves a clip of the last elapsed seconds or minutes.

## ✨ Highlights

* **Zero impact on your games**: Recording is handled 100% by your NVIDIA graphics card.
* **Instant saving**: Clips are generated in a fraction of a second without slowing down your computer[cite: 3].
* **Lightweight and portable**: No complex installation required; the application runs directly[cite: 3].

## 📥 Download

The latest ready-to-use version is available in the **[Releases](../../releases)** section of the repository[cite: 3]: download `ScreenClipTool.exe` (a single, self-contained executable, no .NET runtime needed), place `ffmpeg.exe` and `ffprobe.exe` next to it (see prerequisites below), and run[cite: 3].

---

## 🚀 Quick Start Guide

### 1. Hardware and Software Prerequisites
* **System**: Windows 10 or Windows 11[cite: 3].
* **Graphics Card**: NVIDIA GeForce (RTX 40xx/50xx series recommended for the AV1 format, older cards are supported through automatic fallback to HEVC)[cite: 3].
* **FFmpeg** (the video engine): 
  1. Download a recent "full" build from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/)[cite: 3].
  2. Extract the downloaded file and place **`ffmpeg.exe`** (and ideally `ffprobe.exe`) directly in the same folder as the `ScreenClipTool.exe` application[cite: 3].

### 2. Launching
Double-click `ScreenClipTool.exe`[cite: 3]. 
* A dark window opens showing a **REC** indicator (confirming the background recording is active) and the list of your recent clips[cite: 3].
* If you close this window, the application remains active and minimizes to the notification area (next to the Windows system clock)[cite: 3]. Double-clicking the icon will reopen it[cite: 3].

---

## ⚙️ Configuration and Settings

You can configure everything directly from the application's graphical user interface (Settings button)[cite: 3]. Changes are applied instantly without needing to restart the program[cite: 3].

### Hotkeys
You can configure multiple keys for different durations (the list is sorted from shortest to longest duration)[cite: 3]. For example:
* `Alt + X` ➔ Save the last 15 seconds[cite: 3].
* `Alt + C` ➔ Save the last 10 minutes[cite: 3].
* `F11` ➔ Save the entire available memory buffer[cite: 3].

> ⚠️ **Important Note**: If you choose a single key without a modifier (like `F11` without Alt or Ctrl), this key will be "blocked" by the application and your games will not receive it as long as ScreenClipTool is running[cite: 3]. Use combinations instead (e.g., `Alt + F11`)[cite: 3].

### Main options in the `config.json` file
For advanced users, a `config.json` file is automatically created next to the executable (or in your `%APPDATA%` folder)[cite: 3]. Here are the main options you can modify[cite: 3]:

| Parameter | Description |
|---|---|
| `output_dir` | The folder where your final clips will be saved (e.g., `"C:/Clips"`)[cite: 3]. |
| `max_buffer_minutes` | The maximum duration of the continuous recording (e.g., `15`)[cite: 3]. Older videos are automatically deleted to prevent filling up your hard drive[cite: 3]. |
| `fps` | Video framerate (`60` or `30`)[cite: 3]. |
| `audio_enabled` | `true` (enabled) or `false` (disabled) to record your PC system sound[cite: 3]. |
| `mic_enabled` | `true` or `false` to include your microphone on a separate audio track[cite: 3]. |
| `output_idx` | If you have multiple displays, `0` stands for the main screen, `1` for the second, etc[cite: 3]. |

---

## 🛠️ For Developers (Compilation)

If you prefer to compile the application yourself, the .NET 8 SDK is required[cite: 3]:

```powershell
# Compile in Release mode:
dotnet build -c Release

# Create a single, self-contained executable (no need to install .NET on the target machine):
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
