# Performance measurement procedure

Results are `UNVERIFIED` until captured on the intended Windows 11 x64 laptop. Record app commit/version, CPU/RAM, microphone, model hashes, power mode, target, date, and raw evidence. Do not treat the prerecorded fixture smoke as a production benchmark.

## Setup

1. Install the local models and enroll the primary voice with clean speech. Use the same microphone and position throughout.
2. Open a disposable Notepad document. Perform three untimed warm-up dictations; then 10 measured runs of the same spoken phrase. Include separate cases with another speaker and overlap; report misses and false accepts as errors, not as excluded outliers.
3. Record shortcut press/release, overlay status, and target field at 60 fps or better. Capture Windows Performance Recorder/Analyzer traces for CPU, memory, audio, inference, and UI dispatcher responsiveness. Preserve raw trace and video files.

## Measurements

For each run report sample values and median, nearest-rank p95, minimum, and maximum. Use a monotonic timestamp/clock in traces when possible; otherwise frame-step video and state its frame-width uncertainty.

| Metric | Start | End | Evidence |
| --- | --- | --- | --- |
| Recording startup | Shortcut down | Input level first changes | Video/trace |
| First authenticated partial | Shortcut down | First verified user-only partial | Worker `firstPartialAudioMs`, correlated trace |
| ASR completion | Shortcut up | Final raw authenticated transcript | History ASR duration and trace |
| Target restoration | ASR final | Captured target validated | UI Automation/ETW trace |
| Insertion | Insertion call | Result returned | History insertion duration |
| End-to-end | Shortcut up | Complete raw text visible | Video/trace |

There is **no cleanup stage**. `total_duration_ticks` in history spans recording start through insertion, not release-to-insertion; do not substitute it. The native worker processes 1.12-second audio chunks and may spend additional time flushing after release. Memory must be measured at idle baseline and after 10, 25, 50, and 100 sessions without restarting, with private bytes/working-set plateau and failures recorded. Inspect dispatcher traces for any stall longer than 100 ms; don't claim UI responsiveness from unit tests alone.

| Claim | Result |
| --- | --- |
| Release-to-insertion distribution | `UNVERIFIED` |
| First partial and ASR finalization distribution | `UNVERIFIED` |
| Other-speaker exclusion with overlap | `UNVERIFIED` |
| UI stalls ≤100 ms | `UNVERIFIED` |
| Stable repeated-session memory | `UNVERIFIED` |
