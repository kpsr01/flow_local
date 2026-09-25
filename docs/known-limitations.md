# Known Limitations

This list describes the current implementation rather than the broader project specification.

## Platform and deployment

- Windows 11 x64 only; projects target .NET 9 and Windows SDK build `10.0.26100.0`.
- Real-world compatibility and performance are not yet certified. The [manual compatibility checklist](manual-compatibility.md) and [performance procedure](performance-measurements.md) remain `UNVERIFIED`.
- Safe insertion is limited to same-user, same-integrity desktop targets. Password fields and protected, elevated, unknown-integrity, stale, or mismatched targets are rejected.

## Models and language

- ASR is English-only, running Sortformer v2.1 and INT8 Multitalker Parakeet via parakeet-rs on CPU. There is no GPU selection or calibration UI. Model files download on first startup; a network is needed once.
- A resident native worker isolates ONNX crashes from the WPF process. A failed session keeps its saved WAV for retry; worker startup and completion can be slower on small CPUs.
- Enroll with at least eight seconds of audible solo speech. Verification needs roughly three seconds of clean sole-speaker frames before identifying a channel; short utterances may yield no transcript. After an initial mismatch, the channel needs two seconds of matching trailing speech before its words are admitted.
- Similar voices, noisy recordings, abrupt speaker changes on a reused diarization channel, or inaccurate Sortformer activity can cause false acceptances, omissions, or wrong-speaker words. This is not security-grade speaker authentication. Do not dictate secrets within earshot of an untrusted speaker.
- Partial transcription remains internal; only the final verified raw transcript is inserted. There is no LLM cleanup, style rewrite, grammar correction, or hallucination repair.

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
- Coding-harness detection needs a foreground terminal exposing fixed TUI chrome, a bundled Pi/Oh My Pi status extension, or accessible model controls in Claude Desktop or ChatGPT/Codex Desktop. Pinned/custom terminal layouts, integrated or remote terminals, and Electron controls that remain inaccessible after FlowLocal requests Chromium accessibility can leave metadata unknown.
- Coding-harness metadata records visible model/effort where detectable, but no model-specific prompt adaptation changes the inserted text. Unknown models stay unknown.

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

- The Settings window contains System status with voice enrollment and local model state. There is no structured log viewer, diagnostic export, or calibration tool.
- Startup failures disable dictation; the overlay reports model errors. Enrollment and dictation cannot record concurrently.
- There is no built-in benchmark runner. Use the linked manual documents and record evidence before making compatibility, latency, UI-freeze, or memory-stability claims.
