# Windows 11 Performance Measurements

Status values: `UNVERIFIED`, `PASS`, `FAIL`, `BLOCKED`. This document is an executable procedure and result sheet; no benchmark has been run and every result begins `UNVERIFIED`.

## Prerequisites and test record

- Tester / UTC date: `UNVERIFIED`
- Windows edition/version/build and architecture: `UNVERIFIED`
- CPU / RAM / GPU and power mode: `UNVERIFIED`
- FlowLocal build/version/configuration: `UNVERIFIED`
- ASR runtime/model/device and cleanup model/backend: `UNVERIFIED`
- Microphone/device/format: `UNVERIFIED`
- Target application/version: `UNVERIFIED`
- Raw evidence folder: `UNVERIFIED`

Use a modern supported Windows 11 laptop on AC power with a fixed power mode. Close unrelated high-load applications and reboot before the run. Install and initialize both local models, verify the microphone, start FlowLocal, then complete three untimed warm-up dictations. Use Notepad for the measured target and the same spoken fixture each time: “The performance check started on Thursday at five thirty PM.” Keep fixture, microphone position, target, and model settings fixed.

Capture:

1. A screen recording at 60 fps or higher showing the shortcut action, overlay, target field, and a millisecond-capable input indicator when available. Preserve the original recording; frame interval is `1000 / fps` ms.
2. FlowLocal History after each session. The app already persists and displays `ASR`, `Cleanup`, `Insertion`, and `Duration` values from `%LOCALAPPDATA%\FlowLocal\flowlocal.db`; use these rather than timing those stages again in documentation. `Duration` is recording duration, while the database `total_duration_ticks` currently spans recording start through completed insertion—not release-to-insertion—so do not report it as release-to-insertion.
3. Windows Performance Recorder/Analyzer (WPR/WPA), Task Manager, or equivalent trace for UI responsiveness and memory. Record tool versions and commands/settings with the evidence.

Run 10 measured sessions after warm-up. For each timed event use a monotonic trace timestamp or video frame number, retain raw start/end evidence, and calculate `end - start`. Report every sample plus median, p95 (nearest-rank), minimum, and maximum; do not discard failures or outliers.

## Timing definitions and procedures

| Metric | Start | End | Source / procedure | Threshold | Result |
| --- | --- | --- | --- | --- | --- |
| Hotkey-to-overlay latency | First shortcut-down event / first frame visibly showing actuation | First frame in which the recording overlay is visible | Step through the screen recording frame-by-frame; subtract frame timestamps. | Overlay visible within **150 ms of recording start**. | `UNVERIFIED` |
| Microphone startup latency | Same shortcut-down timestamp | First non-header PCM sample written to the session WAV (or first audio callback event if a trace exposes it) | Use the saved WAV in `%LOCALAPPDATA%\FlowLocal\Recordings`; correlate its first captured sample/event with the shortcut trace. State clock-correlation method and uncertainty. | No numeric target stated; report distribution. Also assess beginning-audio loss below. | `UNVERIFIED` |
| ASR finalization latency | Shortcut-up event | Raw ASR result completion | Use History **ASR** duration as the stage duration; it measures finalization after capture stops, including the built-in retry path when invoked. Preserve the History screenshot/row. | No numeric target stated; report distribution. | `UNVERIFIED` |
| Cleanup latency | Raw ASR completion | Cleaned result completion | Use History **Cleanup** duration, preserving the History screenshot/row. | No numeric target stated; report distribution. | `UNVERIFIED` |
| Focus restoration latency | Start of target restoration (after cleanup) | Target window is foreground and validated | Capture an ETW/UI Automation trace if available; otherwise frame-step a recording showing focus cues and explicitly mark the start estimate/uncertainty. Current History does not expose this interval, so never substitute another field. | No numeric target stated; report distribution. | `UNVERIFIED` |
| Insertion latency | Insertion call begins after focus restoration | Insertion operation returns | Use History **Insertion** duration, preserving insertion method and History screenshot/row. | Insertion begins as soon as cleaned text is available: verify no unexplained gap between cleanup completion and focus restoration/insertion; no numeric allowance stated. | `UNVERIFIED` |
| Total release-to-insertion latency | Shortcut-up event | First frame in which the complete cleaned transcript is visible in the target | Frame-step the screen recording or correlate shortcut and target-text events in one trace. Do **not** use History `Duration`/`total_duration_ticks`. | No numeric target stated; report distribution. | `UNVERIFIED` |

## Responsiveness and threshold checks

### No lost beginning audio

For each run, begin the fixture immediately on shortcut-down with the word “The.” Replay the saved WAV and compare the raw transcript with the known fixture. Capture the first 500 ms waveform/spectrogram and raw transcript. Pass only when every run contains the audible initial phoneme/word and the raw transcript does not omit it; otherwise record the affected run and artifact. Result: `UNVERIFIED`.

### No UI freezes longer than 100 ms

Record a WPR UI/CPU trace covering model initialization and the 10 sessions, including audio capture, ASR, cleanup, database save/history search, saved-audio playback, and insertion. In WPA inspect the WPF UI thread and identify contiguous periods when it cannot process dispatcher/input work. List every stall over 100 ms with stack/activity and phase. Pass only if none exceed **100 ms**. Trace command/profile and result: `UNVERIFIED`.

### Insertion starts when cleanup is available

For each run correlate cleanup-complete with focus-restoration/insertion-start using a single trace clock. Record the gap and explain any focus restoration work. The requirement states no numeric tolerance; report the raw gap and mark PASS only if insertion begins immediately after required focus validation with no unrelated delay. Result: `UNVERIFIED`.

### Stable memory across repeated sessions

After warm-up, record FlowLocal private working set and committed bytes at idle baseline, then after sessions 10, 25, 50, and 100, forcing no GC and restarting neither app nor models. Use Performance Monitor counters `Process(FlowLocal)\Private Bytes`, `Working Set - Private`, and `.NET CLR Memory(FlowLocal)\# Bytes in all Heaps` where available; sample every second and preserve CSV. Plot/inspect post-session idle plateaus. The requirement supplies no numeric limit: report absolute/percentage change and slope, and mark PASS only when plateaus do not show sustained unbounded growth; otherwise FAIL with trace evidence. Result: `UNVERIFIED`.

### Responsiveness coverage

In the same trace, actively exercise each required phase at least once and record evidence: audio capture `UNVERIFIED`; model initialization `UNVERIFIED`; ASR inference `UNVERIFIED`; cleanup inference `UNVERIFIED`; database operations/history save and search `UNVERIFIED`; audio playback `UNVERIFIED`; history search `UNVERIFIED`. Confirm inference does not block the WPF UI thread: `UNVERIFIED`.

## Sample sheet

Fill milliseconds from raw artifacts; use one row per measured session.

| Run | Overlay | Mic startup | ASR | Cleanup | Focus restore | Insertion | Release-to-insertion | Beginning audio | Max UI stall | Evidence |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: | --- |
| 1–10 | `UNVERIFIED` | `UNVERIFIED` | `UNVERIFIED` | `UNVERIFIED` | `UNVERIFIED` | `UNVERIFIED` | `UNVERIFIED` | `UNVERIFIED` | `UNVERIFIED` | `UNVERIFIED` |
| Median | `UNVERIFIED` | `UNVERIFIED` | `UNVERIFIED` | `UNVERIFIED` | `UNVERIFIED` | `UNVERIFIED` | `UNVERIFIED` | — | — | — |
| p95 | `UNVERIFIED` | `UNVERIFIED` | `UNVERIFIED` | `UNVERIFIED` | `UNVERIFIED` | `UNVERIFIED` | `UNVERIFIED` | — | — | — |
| Min / max | `UNVERIFIED` | `UNVERIFIED` | `UNVERIFIED` | `UNVERIFIED` | `UNVERIFIED` | `UNVERIFIED` | `UNVERIFIED` | — | — | — |

## Final result

- Overlay ≤150 ms: `UNVERIFIED`
- No beginning-audio loss: `UNVERIFIED`
- No UI freeze >100 ms: `UNVERIFIED`
- Insertion begins when cleanup is available: `UNVERIFIED`
- Memory stable across repeated sessions: `UNVERIFIED`
- All seven timing distributions and raw evidence attached: `UNVERIFIED`
- Overall performance result: `UNVERIFIED`
