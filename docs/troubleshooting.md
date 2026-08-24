# Troubleshooting

## Initialization overlay reports a failure

FlowLocal enables dictation only after history, the Moonshine speech model, and the cleanup model initialize. Read the full overlay message, correct the named prerequisite, then exit from the tray menu and restart; the current UI has no general initialization retry command.

### Speech model is missing or not ready

- Confirm the machine is online for the first model download, or that `%LOCALAPPDATA%\FlowLocal\Models\moonshine-streaming-medium` contains `frontend.onnx`, `encoder.onnx`, `adapter.onnx`, `cross_kv.onnx`, `decoder_kv.onnx`, and `tokenizer.json`.
- If a download was interrupted, delete the `.download` partial files (or the whole folder) and restart FlowLocal so the worker fetches them again.
- Ensure the Windows account can write to `%LOCALAPPDATA%\FlowLocal\Models`. FlowLocal owns that directory.
- First initialization loads five ONNX sessions and can take a few minutes on some machines. Do not substitute `FakeAsrService`; it is not wired into production startup.

### “Set FLOWLOCAL_CLEANUP_MODEL_PATH…” or cleanup model not found

Check the variable in the same account/environment used to launch FlowLocal:

```powershell
[Environment]::GetEnvironmentVariable("FLOWLOCAL_CLEANUP_MODEL_PATH", "User")
Test-Path $env:FLOWLOCAL_CLEANUP_MODEL_PATH
```

If the variable is unset, FlowLocal looks for a `.gguf` file in `%LOCALAPPDATA%\FlowLocal\Models` (where the installer places it), preferring a file named `sotto-cleanup-lfm25-350m-q4_k_m.gguf`. If the first command has a path but `$env:` is empty, start a new shell or restart Explorer before launching the app. The value must be a full path to one readable GGUF file, not a directory.

### Cleanup model fails to load

The file may be incomplete or incompatible with LLamaSharp/llama.cpp, or too large for available memory. Rebuild it from the [sotto-cleanup-lfm25-350m](https://huggingface.co/juanquivilla/sotto-cleanup-lfm25-350m) BF16 checkpoint: `python convert_hf_to_gguf.py <checkpoint-dir> --outfile m.gguf --outtype bf16`, then `llama-quantize m.gguf sotto-cleanup-lfm25-350m-q4_k_m.gguf Q4_K_M` (llama.cpp b10603 or newer for LFM2 support). The deployed copy's SHA-256 is `6cd4dfed74fa20121a3144866fdc3853a680b764f21f2eaaa83811c396f00f3a`. Inference runs on CPU by default; setting `FLOWLOCAL_CLEANUP_GPU=1` enables experimental full GPU offload with automatic fallback to CPU.

### Speech recognition fails or the app previously crashed while transcribing

All Moonshine model code runs in the separate `FlowLocal.AsrWorker.exe` process. If recognition stalls, the worker is terminated and respawned automatically, and the dictation fails with a typed error while keeping the saved recording for retry. If every session still reports `AsrFailed`, confirm the six model files above exist and verify a microphone is capturing (Settings > Microphone).

## Microphone problems

### Capture fails or no audio is received

1. In **Windows Settings > System > Sound > Input**, select and test the intended default device.
2. In **Privacy & security > Microphone**, enable microphone access and desktop-app access.
3. Close software holding the microphone in exclusive mode.
4. Reconnect USB/Bluetooth hardware, confirm its recording profile is active, and restart FlowLocal.

The implementation follows the Windows default capture endpoint and requests 16 kHz, 16-bit, mono directly. **Settings > Microphone** can pin a device (with fallback to the default plus a tray notification when the pinned endpoint disappears), refresh the device list, and open Windows sound settings. There is still no level test, resampler, or hot-swap recovery; a device that rejects that format can fail initialization/capture.

### Wrong microphone records

Either change the Windows default recording device in Windows sound settings, or pin the intended device under **Settings > Microphone** by turning off *Follow the Windows default input device* and selecting it. New dictation sessions pick up the preference immediately; a session that is already recording keeps its endpoint.

## Shortcut problems

The default chord is hold Ctrl+Windows, release either modifier to finish, and press Escape while held to cancel. Either left/right Ctrl and Windows key works. The chord can be changed under **Settings > Shortcuts** to any combination of Ctrl, Alt, Shift, and Windows modifiers; the choice is saved to `%LOCALAPPDATA%\FlowLocal\app-settings.json`.

- If nothing appears, verify FlowLocal is running in the notification area and has completed model initialization.
- Another application or Windows shortcut may react to the chord; try a different combination via Settings > Shortcuts. There is no automatic conflict detection.
- With hands-free double-tap enabled (Settings > General), a very quick release waits briefly for a second tap before finalizing; a second tap within the window switches to hands-free recording, shown as "Hands-free recording" with Stop/Cancel buttons on the overlay. Disable the toggle if quick taps feel delayed.
- Run FlowLocal and the target at the same normal user integrity. An elevated target cannot be injected into safely by a normal FlowLocal process; running FlowLocal elevated is not a recommended workaround for ordinary targets.
- If keys appear stuck after a system/security screen transition, release all chord modifiers and retry only after the desktop is active.

## Target detection and style problems

Use **Settings > Application styles > Test current target** while the desired text field/application is foreground. The diagnostics show executable, normalized domain when available, category, source, rule, and detection error.

- Website detection supports Chrome, Edge, and Firefox heuristics and may fail after browser accessibility/UI changes. Failure falls back to application/generic/general rules.
- Only normalized domains are accepted. Enter `mail.example.com`, not `https://mail.example.com/inbox`.
- Executable override keys should be names such as `notepad`, without path or `.exe`.
- Turn off **Detect website domains** to prevent domain classification, or turn off style classification/use the universal category for a consistent fallback.
- **Reset defaults** removes saved override choices.

If `%LOCALAPPDATA%\FlowLocal\application-styles.json` is malformed or unreadable, FlowLocal silently activates defaults and displays a settings diagnostic. Correct it while FlowLocal is closed or reset/save from Settings.

## Cleanup output is unchanged or unsuitable

If the cleanup model throws or its output fails validation, FlowLocal retries once and then inserts the raw ASR transcript while recording `CleanupFailed`. This is deliberate data-preserving fallback, not proof that cleanup succeeded. Inspect the History entry's raw/cleaned text and error, confirm the correct target classification, and verify the configured GGUF. The cleanup model is constrained to transcript cleanup; it is not intended to answer or execute dictated prompts.

## Text is not inserted

Keep the original target field available until processing completes. FlowLocal restores and revalidates the captured target before inserting.

- Click a writable, non-password text field before holding the shortcut.
- Do not close, recreate, navigate away from, or elevate the target during transcription/cleanup.
- Password fields, read-only controls, stale/mismatched focused elements, and higher/unknown-integrity targets are blocked.
- If automatic methods are unsafe or fail, the overlay/history may report clipboard-only fallback. Paste manually with Ctrl+V; FlowLocal must not claim that fallback inserted text.
- Terminal text is inserted but Enter is never sent; commands remain unexecuted.
- Review History for the insertion method and error. Use **Retry insertion** only after selecting a new safe target.

## History, recordings, and recovery

Data is under `%LOCALAPPDATA%\FlowLocal`; use **Open data directory** in Settings.

- Missing old audio can be normal retention behavior (default seven days), while transcript/history details default to thirty days.
- Disabling saved recordings prevents retained audio after retention processing; the app still creates a temporary/recovery WAV for an active session.
- If playback, export, or retry-ASR is unavailable, its WAV may have expired or been cleared.
- On an interrupted session, restart FlowLocal and use the recovery prompt. **Recover** opens History; **Delete** removes the recoverable entry/audio.
- Deletion deliberately keeps a history row when its recording cannot be deleted, so the file path remains available for retry. Close players/file handles or fix permissions and retry.
- **Delete all history** and **Clear recordings** are destructive and require confirmation.

## Build or test failures

Use a .NET 9 SDK and Windows 11 x64. From the repository root:

```powershell
dotnet restore .\FlowLocal.slnx
dotnet build .\FlowLocal.slnx -c Release
dotnet test .\FlowLocal.slnx -c Release
```

The projects target Windows SDK build `10.0.26100.0`. If reference packs cannot be restored, verify NuGet access and that the .NET SDK can acquire that Windows targeting pack. Runtime/model failures are not fixed by the automated tests; follow the prerequisite checks above.

For manual target behavior use the [compatibility checklist](manual-compatibility.md). For latency/resource investigation use the [performance procedure](performance-measurements.md); both currently contain `UNVERIFIED` result fields and must not be quoted as successful runs.
