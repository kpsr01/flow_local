# Known Limitations

This list describes the current implementation rather than the broader project specification.

## Platform and deployment

- Windows 11 x64 only; projects target .NET 9 and Windows SDK build `10.0.26100.0`.
- Real-world compatibility and performance are not yet certified. The [manual compatibility checklist](manual-compatibility.md) and [performance procedure](performance-measurements.md) remain `UNVERIFIED`.
- Safe insertion is limited to same-user, same-integrity desktop targets. Password fields and protected, elevated, unknown-integrity, stale, or mismatched targets are rejected.

## Models and language

- ASR is English-only and hard-coded to Nemotron Speech Streaming EN 0.6B (Q4_K_M GGUF from `handy-computer/nemotron-speech-streaming-en-0.6b-gguf`, run through transcribe.cpp 0.2.3).
- Nemotron inference runs inside the separate `FlowLocal.AsrWorker.exe` process. A stalled or natively-crashing runtime is contained there; the worker is killed and respawned for the next dictation, and a wedged session leaves saved audio for retry.
- Inference is pinned to the CPU backend; there is no GPU selection or tuning. Streaming partials are internal and only the final transcript is inserted.
- The worker keeps a streaming session resident but still finalizes at release; long sessions are bounded by model/runtime limits and the completion timeout rather than an explicit duration limit.
- Raw ASR output has punctuation/capitalization disabled. General cleanup performs that pass; coding cleanup deliberately retains word case and spelling to avoid changing identifiers. It does not correct substantive ASR mistakes. First initialization may require the network when running from source.
- Cleanup requires `LFM2.5-1.2B-Instruct-QAD-Q4_0.gguf`; the packaged build includes llama.cpp, while source runs can use `FLOWLOCAL_LLAMA_SERVER_PATH`. `FLOWLOCAL_CLEANUP_DSPARK=1` additionally requires the DSpark Q4_K_M sidecar and enables speculative decoding.
- Cleanup runs on CPU with a 2048-token context and deterministic sampling. DSpark is an optional throughput path, not a quality change: it must be checked against the paired benchmark because speculative decoding can be slower on some short inputs.
- Coding cleanup constrains decoding to the original substantive words and order; the local model selects punctuation and layout, whose quality is not guaranteed. General cleanup remains prompt-driven. Both retain validation/retry/raw fallback.
- Greedy decoding uses the model card's `repetition_penalty=1.05`. Rare n-gram loops remain possible on pathological verbatim-repetition inputs; the card's own guidance treats this penalty setting as the production default and `CleanupResultValidator` plus the raw-transcript fallback bound the damage.
- Cleanup retries once and then falls back to the raw transcript. A fallback session is marked `CleanupFailed`; unchanged text is not a cleanup-success claim.

## Microphone and shortcut

- Capture follows the Windows default recording endpoint by default. Settings can pin a specific device and list active devices; a pinned device that disappears falls back to the Windows default with a tray notification. There is still no device test button, live settings-page meter, or noise-floor calibration.
- Capture requests 16 kHz, 16-bit, mono directly; there is no native-format capture/resampling path. Hardware/drivers that reject this format may fail.
- Microphone removal/profile changes and exclusive-mode conflicts are surfaced as session errors rather than managed with automatic recovery.
- The push-to-talk chord is a configurable modifier combination (default Ctrl+Windows) persisted in `%LOCALAPPDATA%\FlowLocal\app-settings.json`. Only modifier chords are supported; there is no non-modifier trigger key, no mouse-button binding, and no automatic conflict detection. Escape cancels only while the chord is held.
- The overlay now displays a live input-level bar and recording duration while listening. The settings page itself has no meter or test-recording control.
- Hands-free mode is available by double-tapping the push-to-talk chord within a configurable window; the second tap stops recording, Stop/Cancel buttons appear on the overlay, and Escape still cancels. A separate dedicated hands-free shortcut is not implemented.

## Context and styling

- Browser-domain detection is heuristic and currently targets Chrome, Edge, and Firefox. Browser accessibility/UI changes, unusual profiles, and unsupported browsers can reduce detection to a generic/general classification.
- FlowLocal records only normalized domains, never URL paths or query strings. It therefore cannot classify by page path or inspect full page content.
- Built-in domain/application tables are finite. Unknown targets use control hints, generic browser, or the universal/general fallback; users must add overrides for other targets.
- Overrides can choose categories/styles but do not add arbitrary classifier code or URL rules. Full URLs are rejected.
- Cleanup is for faithful formatting, not summarization, answering questions, executing prompts, or inventing code and plans.
- Codex and Claude Code detection requires the bundled adapters and a foreground window exposing the harness's application-set title. Pinned tab titles, some integrated IDE terminals, remote sessions, and incompatible harness versions can hide that signal. Model IDs are not allowlisted; unavailable metadata remains unknown and unlisted IDs use general rewrite-only guidance.
- Codex truncates long title fields. A full model ID then requires matching hook metadata younger than five minutes; review the hooks in `/hooks`. Claude refreshes its timestamped status line every 30 seconds. Detection reports the harness-selected model, not the actual backend behind an opaque gateway.

## Insertion

- UI Automation direct insertion supports safe writable single-line value controls; other nonterminal controls fall through to transactional clipboard paste and then Unicode `SendInput`. Terminal targets use paste only before clipboard-only recovery.
- Target applications can block simulated input, expose incomplete accessibility metadata, intercept paste, or recreate their focused control while models run. In these cases insertion may fail or leave text available for manual clipboard paste.
- FlowLocal never sends Enter after insertion, including terminals and chat composers.
- Clipboard restoration is best effort when another application changes the clipboard concurrently. Ambiguous side effects stop further automatic attempts to avoid duplicate text.

## Privacy, history, and recovery

- History, metadata, transcripts, style settings, and WAV files are local but not encrypted by FlowLocal; Windows account permissions are the protection boundary.
- A WAV file is created for every active/recoverable session even when retained-audio saving is disabled; retention processing removes it after the session according to settings.
- Defaults retain audio seven days and transcript/history details thirty days. `Forever` can consume unbounded disk space until the user deletes data.
- Domain metadata is normalized, but application names, executable names, transcripts, timings, errors, and recording audio can still be sensitive.
- A failed recording-file deletion intentionally preserves the corresponding history row/path for retry; deletion may therefore be partial until locks or permissions are fixed.
- Recovery identifies interrupted nonterminal sessions and opens History or deletes them; it does not automatically resume an in-flight inference/insertion operation.

## UI and diagnostics

- The Settings window contains General, Shortcuts, Microphone, Application styles, History/privacy, and Models-and-diagnostics sections. Appearance settings and a comprehensive diagnostics page (structured log viewer, diagnostics export) are absent; the diagnostics section shows version/runtime/model/microphone status only.
- Startup failure disables dictation, but the shortcut-time unavailable message names the speech model generically even when another initialization prerequisite (such as the cleanup model) caused the failure.
- Model readiness is represented by startup overlay success/failure plus the Models-and-diagnostics settings section rather than a full readiness page with load/unload controls.
- There is no built-in benchmark runner. Use the linked manual documents and record evidence before making compatibility, latency, UI-freeze, or memory-stability claims.
