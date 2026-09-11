# Known Limitations

This list describes the current implementation rather than the broader project specification.

## Platform and deployment

- Windows 11 x64 only; projects target .NET 9 and Windows SDK build `10.0.26100.0`.
- Real-world compatibility and performance are not yet certified. The [manual compatibility checklist](manual-compatibility.md) and [performance procedure](performance-measurements.md) remain `UNVERIFIED`.
- Safe insertion is limited to same-user, same-integrity desktop targets. Password fields and protected, elevated, unknown-integrity, stale, or mismatched targets are rejected.

## Local models and language

- The default local ASR is English-only Canary 180M Flash (Q4_K_M GGUF from `handy-computer/canary-180m-flash-gguf`, run through transcribe.cpp). Cloud ASR is separately selectable.
- All Canary GGUF inference runs inside the `FlowLocal.AsrWorker.exe` companion process. A stalled or natively-crashing model runtime is contained there; the worker is killed and transparently respawned for the next dictation, and a wedged session degrades into a typed ASR failure with saved audio for retry.
- Inference is pinned to the CPU backend (exact request, no device fallback); no GPU selection or tuning exists.
- The worker buffers session audio in memory and transcribes once at release; very long sessions are bounded by the model's ~400-second input limit and the completion timeout rather than an explicit duration limit.
- Raw ASR output is lowercase without punctuation or capitalization (PnC disabled; Sotto restores formatting), and the first startup may require the network while the worker downloads the GGUF; first initialization is retried once in a fresh worker process before failing visibly.
- When running from source without the installer, the cleanup GGUF (`sotto-cleanup-lfm25-350m-q4_k_m.gguf`) must be supplied manually: one file in `%LOCALAPPDATA%\FlowLocal\Models` or an explicit path in `FLOWLOCAL_CLEANUP_MODEL_PATH`. There is no in-app download flow, file picker, or model discovery beyond that directory, and the upstream model repo publishes no GGUF (convert from BF16 with llama.cpp).
- The cleanup model runs through LLamaSharp's CPU backend by default (`GpuLayerCount = 0`) with an 8192-token context and greedy (temperature-0) decoding per the model card, with no runtime tuning UI. Setting `FLOWLOCAL_CLEANUP_GPU=1` opts into full GPU offload, which was observed to be slower and process-fatal on an MX450-class GPU; it remains available for experimentation and falls back to CPU automatically.
- The prompt adapter sends Sotto's exact training format — a plain `### Input:` / `### Output:` completion block with no chat template and no system prompt — since the base-model fine-tune was trained on that single fixed layout. The model covers English only. App-level output-style selection remains in the UI but is not forwarded to the cleanup model; any formatting in its output emerges from the model itself rather than being forced.
- Greedy decoding uses the model card's `repetition_penalty=1.05`. Rare n-gram loops remain possible on pathological verbatim-repetition inputs; the card's own guidance treats this penalty setting as the production default and `CleanupResultValidator` plus the raw-transcript fallback bound the damage.
- Cleanup retries once and then falls back to the raw transcript. A fallback session is marked `CleanupFailed`; unchanged text is not a cleanup-success claim.

## AssemblyAI cloud providers

- Cloud ASR uses Sync STT `universal-3-5-pro`, not the async transcription API. The app requests English and accepts clips from 80 ms to 120 seconds at 16 kHz, 16-bit mono. Longer clips fail; they are not silently chunked or truncated.
- Both providers require a valid API key and network access. AssemblyAI usage is billable; authorization, quota/rate-limit, and service failures can occur. No key is bundled. Settings can save a masked key using Windows user-level encryption; key/provider changes require a restart, and a copied credential may need re-entry on another Windows account or machine.
- The Gateway defaults to `qwen3.5-4b-32k-fast`; model access can vary by account. Rewrites have a five-second deadline and no automatic retry. Failure, timeout, or rejected output preserves the exact raw transcript.
- Context is best-effort: process, captured title, focused-control type, and normalized browser domain. Browser address-bar lookup waits at most 250 ms before trying conservative captured-title hints. There is no selected-text, nearby-content, page, or project indexing.
- Category overrides choose the fixed cloud rewrite profile. Arbitrary style text is not injected into the system prompt. Prompts prohibit answering/acting and invented details, but model formatting and exact meaning preservation need real-recording evaluation; validation cannot prove semantic equivalence.
- Live synthetic-speech checks found that terminal recognition can add a sentence-ending period (`git status` → `git status.`), which the fast Gateway model may retain. Prompt experiments that removed it also risked changing quoted literals, so they were not retained. Review terminal text before pressing Enter; neither recognition nor rewriting is a command-syntax guarantee. Raw fallback intentionally preserves recognition text, including its mistakes.
- Vocabulary/correction hints are manually configured, not learned automatically. Empty, duplicate, control-character-containing, or overlong hints are ignored and the total is bounded to 2,048 characters.
- Live STT/LLM quality and post-release latency require a configured key and representative recordings. Synthetic protocol/fallback checks and connection warming do not establish those results.
- Local history deletion/retention does not delete cloud-side audio or transcripts; consult the AssemblyAI account/provider policy before sending sensitive material.

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
- Cleanup is meant for faithful formatting, not summarization, answering questions, executing prompts, or guaranteed code generation.

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
