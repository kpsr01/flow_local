# FlowLocal

FlowLocal is a Windows 11 x64 WPF dictation application. Hold the global shortcut, speak into the Windows default recording device, and release to run English speech recognition locally, clean the transcript with a local Mumble cleanup GGUF model, classify the active target, and insert the result. The application is still awaiting the documented manual compatibility and performance runs; see [Known limitations](docs/known-limitations.md).

## System requirements

- Windows 11 x64. The projects target `net9.0-windows10.0.26100.0`; Windows 10 is not a supported target.
- .NET 9 SDK to build or run from source. A self-contained packaged build does not require a separately installed .NET runtime.
- A working Windows recording device and microphone permission for desktop applications.
- Disk space and memory for the app, the Moonshine streaming-medium ONNX models (~296 MB in `%LOCALAPPDATA%\FlowLocal\Models\moonshine-streaming-medium`), and the Mumble cleanup GGUF (~336 MB).
- Internet access for initial NuGet restore and the first speech-model download. Dictation inference is local after those assets are installed.

## Speech model

ASR runs [Moonshine streaming-medium](https://huggingface.co/moonshine-ai/moonshine-streaming-medium) through ONNX Runtime (CPU) inside the `FlowLocal.AsrWorker.exe` companion process. The worker loads `frontend.onnx`, `encoder.onnx`, `adapter.onnx`, `cross_kv.onnx`, and `decoder_kv.onnx` plus `tokenizer.json` from `%LOCALAPPDATA%\FlowLocal\Models\moonshine-streaming-medium`. The installer downloads these files; when running from source, the worker downloads any missing file from Hugging Face at first init, so the first launch may need network access.

## Cleanup model installation

The cleanup stage uses [mumble-cleanup-2stage GGUF by trevornk](https://huggingface.co/trevornk/mumble-cleanup-2stage-GGUF) (`mumble-cleanup-2stage-q4_0.gguf`, Q4_0 — a LoRA fine-tune of Qwen2.5-0.5B-Instruct published upstream as `amitashwini/mumble-cleanup-2stage`) loaded by LLamaSharp's CPU backend. A normal install downloads it into `%LOCALAPPDATA%\FlowLocal\Models` during setup (removing any retired S1-mini GGUF); no environment variable is required.

When running from source without the installer, either place `mumble-cleanup-2stage-q4_0.gguf` into `%LOCALAPPDATA%\FlowLocal\Models` or point at one explicit file:

```powershell
[Environment]::SetEnvironmentVariable(
  "FLOWLOCAL_CLEANUP_MODEL_PATH",
  "C:\Models\mumble-cleanup-2stage-q4_0.gguf",
  "User")
```

Restart the shell or Explorer-launched application after changing the user environment variable. There is no in-app model picker. The app sends every transcript through mumble-cleanup's official system prompt verbatim over the plain Qwen2.5 chat template (no style control line and no think block — extra prompting degrades this fine-tune), decodes greedily at temperature 0 with `max_new_tokens ~= 1.3 x input_tokens + 32`, and keeps the model loaded between requests on an 8192-token context; set `FLOWLOCAL_CLEANUP_GPU=1` to experiment with full GPU offload (it falls back to CPU automatically).

## Build instructions

From the repository root in PowerShell:

```powershell
dotnet restore .\FlowLocal.slnx
dotnet build .\FlowLocal.slnx -c Release
```
To create a release package, run the normal installer mode:

```powershell
.\pack.ps1 -Configuration Release -Version 1.0.0
```

Normal mode requires Inno Setup 6 `ISCC`. It writes `artifacts\installer\FlowLocal-1.0.0-win-x64-setup.exe` for the command above.

For a portable package without Inno Setup, run:

```powershell
.\pack.ps1 -Configuration Release -Version 1.0.0 -PortableOnly
```

Portable-only mode does not require Inno Setup 6 `ISCC`; its publish output is under `artifacts\publish\win-x64`.

The app project is `src\FlowLocal.App\FlowLocal.App.csproj`; the target runtime identifier is `win-x64`.

## Run instructions

After installing the speech and cleanup models:

```powershell
dotnet run --project .\src\FlowLocal.App\FlowLocal.App.csproj -c Release
```

FlowLocal starts in the notification area. Right-click its tray icon for **Settings**, **History**, or **Exit**; double-click it to open Settings. Settings covers General (hands-free double-tap), Shortcuts, Microphone, Application styles, History and privacy, and Models-and-diagnostics. Do not run FlowLocal elevated when dictating into ordinary desktop applications, and do not expect it to insert into a higher-integrity target.

## First-run setup

1. Install FlowLocal normally (the installer downloads the Moonshine ONNX files and the cleanup GGUF), or run once from source with network access so the worker can fetch the speech model into `%LOCALAPPDATA%\FlowLocal\Models\moonshine-streaming-medium`, and place `mumble-cleanup-2stage-q4_0.gguf` in `%LOCALAPPDATA%\FlowLocal\Models` or set `FLOWLOCAL_CLEANUP_MODEL_PATH` as shown above.
2. In Windows, select and test the intended default input device and allow desktop-app microphone access.
3. Start FlowLocal and wait for the initialization overlay to disappear. The worker may download and warm up the Moonshine model on this first run; the cleanup model is then loaded from its discovered or configured file.
4. Open **Settings**, review Application styles and History/privacy defaults, then use **Test current target** while the intended target is active.
5. Focus a writable text field, hold Ctrl+Windows while speaking, and release either key to transcribe, clean, and insert. Press Escape while held to cancel.

Initialization errors remain visible in the overlay. There is no in-app model installer or retry button; correct the prerequisite or path and restart the app.

## Microphone setup

FlowLocal records from the Windows default recording device by default via WASAPI at 16 kHz, 16-bit, mono. **Settings > Microphone** can pin a specific device (with fallback to the default plus a tray notification when the pinned device disappears), list active devices, and open Windows sound settings. It has no device test button, live settings meter, automatic resampling, or noise-floor calibration.

1. Open Windows **Settings > System > Sound > Input**, choose the intended device as default, and test its level.
2. Open **Settings > Privacy & security > Microphone** and enable microphone access and **Let desktop apps access your microphone**.
3. Close applications using the device exclusively. Reconnect Bluetooth devices before starting FlowLocal and confirm the correct Windows input profile/default.
4. Restart FlowLocal after changing the default device. If capture fails, the session is recorded as failed and the overlay reports the error.

## Shortcut configuration

The default push-to-talk chord is:

- Hold **Ctrl+Windows** (left or right variants) to begin push-to-talk.
- Release either modifier to stop and process the recording.
- Press **Escape** while recording to cancel.

The chord is a configurable combination of the **Ctrl**, **Alt**, **Shift**, and **Windows** modifiers. Open **Settings > Shortcuts**, check the modifiers to hold, and use **Apply chord**; the choice is persisted in `%LOCALAPPDATA%\FlowLocal\app-settings.json` and survives restarts. Only modifier chords are supported, there are no mouse-button bindings, and Windows may already reserve some combinations.

**Hands-free mode:** enable *Hands-free double-tap recording* in **Settings > General** (window: 150–2000 ms, default 400 ms). Tap the chord twice within that window to keep recording after releasing; tap once more to finish, or use the overlay **Stop**/**Cancel** buttons. Escape still cancels.

## Privacy behavior

- Recording begins only after the explicit shortcut and the overlay indicates listening.
- ASR and cleanup inference run locally. Initial dependency/model acquisition may use the network; the app itself contains no paid/cloud API integration.
- Website detection stores only a normalized domain, never a full URL path or query string. Website detection can be disabled in Settings.
- The app captures active-window/focused-control metadata for classification and safe insertion. It does not read complete page content and blocks direct insertion into password fields or protected/higher-integrity targets.
- History is local. Defaults save audio for 7 days and transcripts/metadata for 30 days. Settings offers 1, 7, 30, 90 days, or Forever, plus **Clear recordings** and **Delete all history**. Setting **Save recordings for retry and playback** off removes audio under retention processing; audio is still written during an active/recoverable session.
- Cleanup failure or invalid output falls back to the raw ASR transcript and marks a cleanup error; it does not send text to a remote service.

## Data storage locations

All mutable data is under `%LOCALAPPDATA%\FlowLocal`:

| Path | Contents |
| --- | --- |
| `%LOCALAPPDATA%\FlowLocal\flowlocal.db` | SQLite history, transcript, target/style metadata, timings, errors, and retention settings |
| `%LOCALAPPDATA%\FlowLocal\Models\*.gguf` | Cleanup model files (downloaded here by the installer) |
| `%LOCALAPPDATA%\FlowLocal\Models\moonshine-streaming-medium\` | Moonshine ONNX graphs and tokenizer (downloaded here by the installer or the worker) |
| `%LOCALAPPDATA%\FlowLocal\Recordings\<session-id>.wav` | Recoverable/session audio and retained recordings |
| `%LOCALAPPDATA%\FlowLocal\application-styles.json` | Application/domain classification overrides and classification switches |

A manually configured cleanup GGUF remains at the path supplied by `FLOWLOCAL_CLEANUP_MODEL_PATH`. Use **Open data directory** in Settings to open FlowLocal's local data folder.

## Application styles and sample configuration

Built-in categories are `Email`, `WorkMessaging`, `PersonalMessaging`, `Document`, `AiChat`, `CodeEditor`, `Terminal`, and `General`. Resolution prioritizes saved domain overrides, executable overrides, known normalized domains, known applications, focused-control hints, a generic-browser rule, then `General`. Settings can create overrides safely; editing the JSON while FlowLocal is closed is also supported by the loader.

Example `%LOCALAPPDATA%\FlowLocal\application-styles.json`:

```json
{
  "StyleClassificationEnabled": true,
  "WebsiteDetectionEnabled": true,
  "UniversalDefaultCategory": "General",
  "UniversalDefaultStyle": null,
  "DomainOverrides": {
    "example.com": {
      "Category": "WorkMessaging",
      "Style": {
        "Category": "WorkMessaging",
        "Tone": "concise and professional",
        "Structure": "short conversational message",
        "UseStandardCapitalization": true,
        "EnableParagraphs": true,
        "EnableLists": false,
        "EnableEmailFormatting": false,
        "PreserveTechnicalTokens": true,
        "UseSmartPunctuation": true
      }
    }
  },
  "ExecutableOverrides": {
    "notepad": {
      "Category": "Document",
      "Style": {
        "Category": "Document",
        "Tone": "clear and neutral",
        "Structure": "polished prose",
        "UseStandardCapitalization": true,
        "EnableParagraphs": true,
        "EnableLists": true,
        "EnableEmailFormatting": false,
        "PreserveTechnicalTokens": true,
        "UseSmartPunctuation": true
      }
    }
  }
}
```

Domain keys are normalized host names; full URLs are rejected. Executable keys are normalized names without a path or `.exe`. Invalid/unreadable JSON is ignored and defaults become active. See [Architecture](docs/architecture.md) for the complete classification flow.

## Troubleshooting

See [Troubleshooting](docs/troubleshooting.md) for startup, model, microphone, shortcut, target detection, insertion, history, and recovery problems.

## How to run tests

From the repository root:

```powershell
dotnet test .\FlowLocal.slnx -c Release
```

The automated suite is `tests\FlowLocal.Core.Tests\FlowLocal.Core.Tests.csproj`. It covers core state, audio files/recovery, ASR backend and cleanup prompt contracts, cleanup/fallback validation, classification, active-target safeguards, insertion/clipboard behavior, retention/history, and UI smoke paths. Automated tests do not replace real Windows compatibility or latency measurements:

- [Windows 11 manual compatibility checklist](docs/manual-compatibility.md)
- [Windows 11 performance measurement procedure](docs/performance-measurements.md)

Both manual result sheets are currently `UNVERIFIED`; do not treat them as pass claims.

## Additional documentation

- [Architecture](docs/architecture.md)
- [Troubleshooting](docs/troubleshooting.md)
- [Known limitations](docs/known-limitations.md)
- [Manual compatibility checklist](docs/manual-compatibility.md)
- [Performance measurement procedure](docs/performance-measurements.md)
