# FlowLocal

FlowLocal is a Windows 11 x64 WPF dictation application. Hold the global shortcut to snapshot the destination and record speech; release to transcribe, format the dictated text, and insert at the captured cursor. Choose local Canary/Sotto inference or AssemblyAI Sync STT and LLM Gateway. This is dictation, not an agent: rewriting must preserve the request, not answer or execute it. Real-world compatibility and performance remain subject to the documented manual runs; see [Known limitations](docs/known-limitations.md).

## System requirements

- Windows 11 x64. The projects target `net9.0-windows10.0.26100.0`; Windows 10 is not a supported target.
- .NET 9 SDK to build or run from source. A self-contained packaged build does not require a separately installed .NET runtime.
- A working Windows recording device and microphone permission for desktop applications.
- For local inference, disk space and memory for Canary 180M Flash (~133 MB) and the Sotto cleanup GGUF (~229 MB).
- Internet access for initial NuGet restore. Local inference needs model assets installed first; AssemblyAI inference needs an API key and network access for every dictation.

## AssemblyAI cloud dictation

Open **Settings > Models and diagnostics**:

1. Paste your **AssemblyAI API key** into the masked field.
2. Choose **AssemblyAI — Sync STT** for speech recognition and **AssemblyAI — LLM Gateway** for rewriting.
3. Click **Save key and providers**, exit FlowLocal from the tray, and reopen it.

The key and provider choices survive restarts; no shell commands are needed. The key is encrypted with Windows DPAPI for the current Windows user in `%LOCALAPPDATA%\FlowLocal\app-settings.json`, not stored as plaintext. Clear the key field and save to remove the saved credential. Other processes running as the same Windows user can decrypt it; re-enter the key if copied settings cannot be decrypted.

Environment configuration remains available when the corresponding saved setting is absent. Saved provider choices and the saved key take precedence:

| Variable | Values and default |
| --- | --- |
| `FLOWLOCAL_ASR_PROVIDER` | `local` (default) or `assemblyai` |
| `FLOWLOCAL_REWRITE_PROVIDER` | `sotto` or `assemblyai`; defaults to AssemblyAI when cloud ASR is selected, otherwise Sotto |
| `ASSEMBLYAI_API_KEY` | Key fallback when no saved credential exists; either cloud provider requires a key |
| `FLOWLOCAL_ASSEMBLYAI_LLM_MODEL` | Optional Gateway model override; default `qwen3.5-4b-32k-fast` |

Exit and restart FlowLocal after changing the key or providers. Clearing the saved key restores environment-key fallback; it does not remove an environment variable. For A/B comparison, keep AssemblyAI speech recognition and select **Local — Sotto** for rewriting; install the local cleanup model below. With both providers set to AssemblyAI, no local model is loaded or downloaded at runtime. Normal installer packaging still includes local models.

Speech uses [Sync STT](https://www.assemblyai.com/docs/api-reference/sync-api/transcribe), model `universal-3-5-pro`, with one multipart PCM request after release—not asynchronous upload/polling. A reusable connection is [pre-warmed](https://www.assemblyai.com/docs/sync-stt/connection-pre-warming) at startup and recording start. Clips must be 80 ms–120 seconds at the app's 16 kHz, 16-bit mono format.

Recognition receives a short destination-specific prompt plus optional keyterms. The separate [LLM Gateway](https://www.assemblyai.com/docs/llm-gateway/quickstart) request formats email, chat, coding instructions, terminal text, document prose, or generic text. It is instructed not to invent information, answer questions, solve coding requests, or act. A five-second rewrite timeout, HTTP error, malformed/truncated output, or validation failure uses the exact raw transcript without another Gateway attempt. STT errors fail normally rather than pretending no speech was detected.

Optional vocabulary is configured while FlowLocal is closed by adding fields to the existing `%LOCALAPPDATA%\FlowLocal\app-settings.json` (retain its other settings):

```json
"Vocabulary": ["Priya", "Acme", "OAuth", "login.ts"],
"RememberedCorrections": { "pre ya": "Priya", "login dot t s": "login.ts" }
```

Correction values are recognition hints, not string replacements. Corrections are manually maintained; there is no automatic learning or new vocabulary UI. Hints are trimmed, deduplicated, and bounded to 2,048 total characters. A known application name and short filename from a code-editor title may also be included. No selected-text, nearby-content, page, or project scraping is performed.

Debug builds emit `[Dictation]` messages to debugger output: destination label, profile, provider, audio duration, STT/rewrite/insertion duration, total post-release time, and fallback status. They do not log transcript text, full titles, keyterms, or API keys. Use real recordings and a configured key for latency and formatting comparisons; synthetic protocol checks are not model-quality measurements.

## Local speech model

ASR runs [Canary 180M Flash](https://huggingface.co/handy-computer/canary-180m-flash-gguf) (Q4_K_M GGUF) through [transcribe.cpp](https://github.com/handy-computer/transcribe.cpp) (CPU backend) inside the `FlowLocal.AsrWorker.exe` companion process. The worker loads `canary-180m-flash-Q4_K_M.gguf` from `%LOCALAPPDATA%\FlowLocal\Models\canary-180m-flash-gguf`, keeps the model warm between dictations, and decodes greedily with punctuation/capitalization, timestamps, and translation disabled — raw lowercase text is passed to the selected rewrite provider. The installer downloads the file; when running from source, the worker downloads it from Hugging Face at first init, so the first launch may need network access.

## Local cleanup model installation

The cleanup stage uses [sotto-cleanup-lfm25-350m](https://huggingface.co/juanquivilla/sotto-cleanup-lfm25-350m) (a full fine-tune of `LiquidAI/LFM2.5-350M-Base`) loaded by LLamaSharp's CPU backend from a Q4_K_M GGUF converted from the repo's BF16 checkpoint with llama.cpp (`convert_hf_to_gguf.py --outtype bf16`, then `llama-quantize Q4_K_M`; the upstream repo publishes no GGUF, so the file must be built or obtained from your own mirror). A normal install places `sotto-cleanup-lfm25-350m-q4_k_m.gguf` into `%LOCALAPPDATA%\FlowLocal\Models` during setup (removing retired cleanup GGUFs); no environment variable is required.

When running from source without the installer, either place `sotto-cleanup-lfm25-350m-q4_k_m.gguf` into `%LOCALAPPDATA%\FlowLocal\Models` or point at one explicit file:

```powershell
[Environment]::SetEnvironmentVariable(
  "FLOWLOCAL_CLEANUP_MODEL_PATH",
  "C:\Models\sotto-cleanup-lfm25-350m-q4_k_m.gguf",
  "User")
```

Restart the shell or Explorer-launched application after changing the user environment variable. There is no in-app model picker. When Sotto is selected, the app sends each transcript through its exact training format — a plain `### Input:` / `### Output:` completion block with no chat template and no system prompt — decodes greedily at temperature 0 with the model card's recommended `repetition_penalty=1.05` and `max_new_tokens = max(900, 1.5 x input_words)` capped at the next `###` marker, and keeps the model loaded between requests on an 8192-token context; set `FLOWLOCAL_CLEANUP_GPU=1` to experiment with full GPU offload (it falls back to CPU automatically).

## Build instructions

From the repository root in PowerShell:

```powershell
dotnet restore .\FlowLocal.slnx
dotnet build .\FlowLocal.slnx -c Release
```
To create a release package, run the normal installer mode:

```powershell
.\pack.ps1 -Configuration Release -Version 1.2.0
```

Normal mode requires Inno Setup 6 `ISCC`. It writes `artifacts\installer\FlowLocal-1.2.0-win-x64-setup.exe` for the command above.

For a portable package without Inno Setup, run:

```powershell
.\pack.ps1 -Configuration Release -Version 1.2.0 -PortableOnly
```

Portable-only mode does not require Inno Setup 6 `ISCC`; its publish output is under `artifacts\publish\win-x64`.

The app project is `src\FlowLocal.App\FlowLocal.App.csproj`; the target runtime identifier is `win-x64`.

## Updates

The app can update itself online. Two placeholders must point at the real release host before shipping:

1. `UpdateService.ManifestUrl` in `src/FlowLocal.App/UpdateService.cs` — the hosted `latest.json`. For GitHub Releases use `https://github.com/<owner>/<repo>/releases/latest/download/latest.json` (GitHub serves the newest release's asset).
2. `-ReleaseDownloadUrl` in `pack.ps1` — the per-version installer URL template.

To ship an update: bump the version, run `.\pack.ps1 -Configuration Release -Version X.Y.Z`, then publish a release tagged `vX.Y.Z` attaching both files from `artifacts\installer`: the setup exe and `latest.json` (which pack.ps1 generates with the installer's SHA-256). The app verifies the downloaded installer against that hash before running it.

For users: the tray menu has **Check for updates**; the app also checks quietly 30 seconds after startup and shows a tray notification when a newer version exists. Installing downloads the setup, verifies it, exits FlowLocal, and runs the installer silently — history, recordings, and models under `%LOCALAPPDATA%\FlowLocal` are preserved, and the app relaunches afterwards.

## Uninstall

From inside the app: **Settings > Models and diagnostics > Uninstall FlowLocal**. It confirms once, exits, and runs the setup program's silent uninstaller, which removes the program files and all local data under `%LOCALAPPDATA%\FlowLocal` — history, recordings, settings, and both downloaded models — with no further prompts.

The standard Windows entry (**Settings > Apps > FlowLocal**, or *Uninstall* in the Start-menu group) also works; it asks whether to delete local history and recordings and defaults to keeping them for a future installation. The in-app option always removes everything.

## Run instructions

After configuring AssemblyAI as above, or installing the models for the default local providers:

```powershell
dotnet run --project .\src\FlowLocal.App\FlowLocal.App.csproj -c Release
```

FlowLocal starts in the notification area. Right-click its tray icon for **Settings**, **History**, or **Exit**; double-click it to open Settings. Settings covers General (hands-free double-tap), Shortcuts, Microphone, Application styles, History and privacy, and Models-and-diagnostics. Do not run FlowLocal elevated when dictating into ordinary desktop applications, and do not expect it to insert into a higher-integrity target.

## First-run setup

1. Configure the AssemblyAI providers above, or install the local speech and cleanup models. A local source run can download Canary; Sotto needs its GGUF in `%LOCALAPPDATA%\FlowLocal\Models` or `FLOWLOCAL_CLEANUP_MODEL_PATH`.
2. In Windows, select and test the intended default input device and allow desktop-app microphone access.
3. Start FlowLocal and wait for the initialization overlay to disappear. Only selected providers initialize. Local model loading may take time; unavailable cleanup does not block speech capture and falls back to raw text.
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
- The default local providers keep inference on-device; initial dependency/model acquisition may use the network. Selecting AssemblyAI ASR sends audio, a short recognition prompt, and keyterms to Sync STT. Selecting AssemblyAI rewriting sends the raw transcript and a category-specific formatting prompt to LLM Gateway.
- Website detection stores only a normalized domain, never a full URL path or query string. Website detection can be disabled in Settings.
- The app captures active-window/focused-control metadata for classification and safe insertion. It does not read complete page content and blocks direct insertion into password fields or protected/higher-integrity targets.
- History is local. Defaults save audio for 7 days and transcripts/metadata for 30 days. Settings offers 1, 7, 30, 90 days, or Forever, plus **Clear recordings** and **Delete all history**. Setting **Save recordings for retry and playback** off removes audio under retention processing; audio is still written during an active/recoverable session.
- Cloud processing is subject to AssemblyAI's account/provider policies. Local history retention or deletion does not delete cloud-held data. Do not select cloud providers for material that must remain on-device.
- Cleanup failure or invalid output falls back to the raw ASR transcript and marks a cleanup error. It does not silently switch providers or invoke an agent.

## Data storage locations

All mutable data is under `%LOCALAPPDATA%\FlowLocal`:

| Path | Contents |
| --- | --- |
| `%LOCALAPPDATA%\FlowLocal\flowlocal.db` | SQLite history, transcript, target/style metadata, timings, errors, and retention settings |
| `%LOCALAPPDATA%\FlowLocal\Models\*.gguf` | Cleanup model files (downloaded here by the installer) |
| `%LOCALAPPDATA%\FlowLocal\Models\canary-180m-flash-gguf\` | Canary 180M Flash Q4_K_M GGUF (downloaded here by the installer or the worker) |
| `%LOCALAPPDATA%\FlowLocal\Recordings\<session-id>.wav` | Recoverable/session audio and retained recordings |
| `%LOCALAPPDATA%\FlowLocal\application-styles.json` | Application/domain classification overrides and classification switches |
| `%LOCALAPPDATA%\FlowLocal\app-settings.json` | Shortcut, microphone, overlay, vocabulary, correction and provider settings; optional Windows DPAPI-encrypted AssemblyAI key |

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
