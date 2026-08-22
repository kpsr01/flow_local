# Coding Agent Prompt: Local Wispr Flow–Style Dictation App for Windows 11

You are a senior Windows desktop engineer and local-AI engineer. Build a production-quality, open-source Windows 11 desktop application that replicates the core dictation experience of Wispr Flow while running locally and without paid APIs.

The application must provide system-wide push-to-talk dictation, local speech recognition, intelligent transcript cleanup, application-aware output styling, dependable text insertion, and local history and recovery.

Do not implement the deferred features listed at the end of this specification.

---

## 1. Product Goal

Build a Windows 11 application with this core workflow:

```text
User focuses a text field
→ presses and holds a global push-to-talk shortcut
→ speaks naturally, including fillers and self-corrections
→ releases the shortcut
→ local ASR produces the raw transcript
→ S1-mini cleans and formats the transcript
→ the app detects the active application or website
→ the app selects an appropriate output style
→ the cleaned text is inserted into the original text field
→ the recording and transcript are saved to local history
```

The product must work without cloud inference, subscriptions, API keys, or paid services.

Once model files and required runtimes have been installed, normal dictation must work offline.

---

## 2. Target Platform

Target:

```text
Operating system: Windows 11
Primary architecture: x64
Runtime: .NET 9
Desktop UI: WPF
Language: C#
```

Use WPF because the application needs:

- Reliable system-tray support.
- Global keyboard and mouse hooks.
- Non-activating floating windows.
- Direct access to Win32 APIs.
- Windows UI Automation.
- Clipboard and input simulation.
- Straightforward self-contained deployment.

Keep platform-specific code isolated behind interfaces so that ARM64 support can be added later.

---

## 3. Mandatory Technology Stack

Use the following stack unless a component proves technically impossible. Document any substitution clearly.

### Application

```text
C# / .NET 9
WPF
Microsoft.Extensions.DependencyInjection
Microsoft.Extensions.Hosting
Microsoft.Extensions.Logging or Serilog
SQLite
Entity Framework Core or Microsoft.Data.Sqlite
NAudio
Windows UI Automation
Win32 APIs through P/Invoke
```

### Audio Capture

Use NAudio with Windows WASAPI.

Preferred capture format:

```text
16,000 Hz
16-bit PCM
Mono
Little-endian
```

If the selected microphone does not natively provide this format, capture its native format and resample internally.

Audio capture must not block the UI thread.

### Primary ASR Model

Use:

```text
Model: nemotron-speech-streaming-en-0.6b
Runtime: Microsoft Foundry Local
Language: English
Execution: local CPU, GPU, or supported accelerator selected by the runtime
```

Implement the ASR backend behind an interface:

```csharp
public interface IAsrService
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task StartSessionAsync(AsrSessionOptions options, CancellationToken cancellationToken);
    Task PushAudioAsync(ReadOnlyMemory<byte> pcmAudio, CancellationToken cancellationToken);
    Task<AsrResult> CompleteSessionAsync(CancellationToken cancellationToken);
    Task CancelSessionAsync(CancellationToken cancellationToken);
}
```

Nemotron can generate partial results internally, but the default user experience must be final-on-release. Partial results should not be inserted into the target application.

### Transcript Cleanup Model

Use:

```text
Model: superwhisper/s1-mini
Format: GGUF
Quantization: Q4_K_M
Runtime: llama.cpp
C# integration: LLamaSharp or a small managed wrapper around llama.cpp
Execution: completely local
```

Inference configuration:

```text
Temperature: 0
Sampling: greedy or deterministic
Thinking/reasoning mode: disabled
enable_thinking: false
```

Use the official S1-mini system prompt and expected control format from its model documentation. Do not invent a replacement prompt if the model expects a fixed template.

Create a dedicated abstraction:

```csharp
public interface ITranscriptCleaner
{
    Task<CleanTranscriptResult> CleanAsync(
        RawTranscript transcript,
        TranscriptStyle style,
        CancellationToken cancellationToken);
}
```

S1-mini is used only for faithful transcript cleanup and formatting. It must not be asked to perform general rewriting, summarization, or open-ended instruction following.

### Text Insertion

Use a layered insertion strategy:

```text
1. Direct insertion or Windows UI Automation when safe and supported.
2. Clipboard plus simulated Ctrl+V as the general fallback.
3. SendInput character entry as the final fallback.
4. Clipboard-only mode when insertion is impossible.
```

### Local Persistence

Use SQLite for:

- Settings.
- Transcript history.
- Recording metadata.
- Inference metadata.
- Errors and retry state.

Store audio files separately in the application data directory and reference their paths from SQLite.

---

## 4. Required Architecture

Create the application as modular services rather than placing logic in WPF code-behind.

Recommended solution structure:

```text
src/
  FlowLocal.App/
  FlowLocal.Core/
  FlowLocal.Infrastructure/
  FlowLocal.Audio/
  FlowLocal.Asr/
  FlowLocal.Cleanup/
  FlowLocal.Context/
  FlowLocal.Insertion/
  FlowLocal.Persistence/
  FlowLocal.Windows/

tests/
  FlowLocal.Core.Tests/
  FlowLocal.IntegrationTests/
```

Required major components:

```text
ApplicationHost
RecordingStateMachine
GlobalShortcutService
GlobalMouseBindingService
AudioCaptureService
AudioResampler
VoiceActivityMonitor
AsrService
TranscriptCleaner
ActiveTargetTracker
ApplicationContextDetector
BrowserContextDetector
OutputStyleClassifier
TextInsertionService
ClipboardService
HistoryRepository
AudioStorageService
RetryService
MicrophoneService
OverlayController
SettingsService
CrashRecoveryService
```

Use dependency injection throughout the application.

Do not reference WPF controls from the core business-logic projects.

---

## 5. Recording State Machine

Implement recording as an explicit state machine.

Required states:

```text
Idle
Starting
ListeningPushToTalk
ListeningHandsFree
Stopping
Transcribing
Cleaning
Inserting
Completed
Cancelled
Failed
```

Valid example flow:

```text
Idle
→ Starting
→ ListeningPushToTalk
→ Stopping
→ Transcribing
→ Cleaning
→ Inserting
→ Completed
→ Idle
```

Cancellation flow:

```text
ListeningPushToTalk
→ Cancelled
→ Idle
```

Prevent:

- Two recordings running simultaneously.
- Duplicate hotkey-down events starting multiple sessions.
- Hotkey auto-repeat creating multiple sessions.
- Multiple insertion attempts for one transcript.
- A stale transcript being inserted into the wrong application.
- Hands-free mode and push-to-talk mode becoming active simultaneously.

---

# 6. P0 Functional Requirements

All features in this section are mandatory.

## P0.1 System-Wide Dictation

The application must:

- Run in the Windows system tray.
- Continue operating when its settings window is closed.
- Work in text fields across common desktop applications.
- Capture the active window and focused control when recording starts.
- Preserve enough target information to restore focus before insertion.
- Support Win32, WPF, Chromium, Electron, browser, and modern Windows text controls where practical.
- Detect when the original target no longer exists.
- Copy the result to the clipboard when automatic insertion cannot be completed.

Initial manual compatibility targets:

```text
Notepad
Microsoft Word
Outlook
Chrome
Edge
Slack
Discord
Notion
VS Code
Windows Terminal
```

Do not limit dictation to a text box inside the application itself.

---

## P0.2 Global Push-to-Talk

Implement a configurable global push-to-talk shortcut.

Default shortcut:

```text
Ctrl + Win
```

Behavior:

```text
Shortcut key down:
- Capture the active application and focused element.
- Begin microphone capture.
- Begin the ASR session.
- Display the listening overlay.

Shortcut key up:
- Stop microphone capture.
- Finalize ASR.
- Clean the transcript.
- Insert the cleaned text.
```

Requirements:

- Ignore key-repeat events.
- Support left and right modifier keys correctly.
- Do not interfere with unrelated keyboard input.
- Allow users to configure an alternative shortcut.
- Detect likely conflicts with registered shortcuts.
- Support multiple configured bindings for the same action.
- Provide an emergency cancel action using `Esc`.

---

## P0.3 Final-on-Release Transcription

The normal mode must not insert partial text while the user is speaking.

While recording:

- Stream audio to the ASR backend if supported.
- Optionally collect internal partial results.
- Display only a listening indicator, waveform, duration, and microphone level.
- Do not continuously modify the target application.

After release:

1. Finalize the complete ASR transcript.
2. Send the complete transcript to S1-mini.
3. Insert only the final cleaned result.

This behavior is important because corrections near the end of an utterance can change earlier words.

Example:

```text
Spoken:
“Schedule it for Friday—actually, make that Thursday afternoon.”

Inserted:
“Schedule it for Thursday afternoon.”
```

---

## P0.4 Hands-Free Mode

Implement a hands-free recording mode.

Required activation methods:

- A configurable hands-free shortcut.
- Double-tapping the push-to-talk shortcut within a configurable interval.

Hands-free behavior:

```text
First activation:
- Begin recording.
- Remain recording after keys are released.

Second activation:
- Stop recording.
- Transcribe, clean, and insert.
```

The overlay must provide visible:

```text
Stop
Cancel
Recording duration
Microphone level
```

The overlay must not permanently steal focus from the original target.

Clicking `Stop` must return focus to the saved target before insertion.

Clicking `Cancel` must delete or discard the unfinished recording unless crash recovery requires a temporary file.

---

## P0.5 Configurable Keyboard and Mouse Bindings

Provide configurable bindings for:

```text
Push-to-talk
Hands-free mode
Cancel
Paste last transcript
Copy last transcript
Open history
Open settings
```

Support:

- Keyboard combinations.
- Middle mouse button.
- Mouse Button 4.
- Mouse Button 5.
- Multiple bindings for one action.
- Conflict warnings.
- Reset to defaults.
- Disable individual bindings.

Do not use invasive low-level hooks when a registered hotkey is sufficient. Use hooks only for features that cannot be implemented through `RegisterHotKey`.

---

## P0.6 Floating Status Overlay

Implement a small non-activating, always-on-top overlay.

Required visible states:

```text
Ready
Listening
Hands-free recording
Stopping
Transcribing
Cleaning transcript
Inserting
Completed
Cancelled
No speech detected
No text target selected
Microphone unavailable
ASR unavailable
Cleanup model unavailable
Insertion failed
Copied to clipboard
```

Overlay requirements:

- Must not appear in the taskbar.
- Must not take keyboard focus during normal operation.
- Must support multiple monitors.
- Must remain within the visible work area.
- Must remember its position.
- Must optionally dock to screen edges.
- Must support reduced-motion mode.
- Must expose meaningful screen-reader labels.
- Must use text or icons in addition to color.

Provide an option to hide the overlay and use only tray notifications and sounds.

---

## P0.7 Microphone Management

Implement a complete microphone selection and monitoring interface.

Requirements:

- List available Windows recording devices.
- Follow the Windows default microphone by default.
- Allow a specific device to be pinned.
- Remember the selected device.
- Show a real-time input level meter.
- Detect when a device is disconnected.
- Detect a muted or silent device where possible.
- React to default-device changes.
- Handle Bluetooth devices changing audio profiles.
- Handle microphone permission denial.
- Handle an exclusive-mode conflict.
- Provide a button that opens Windows microphone and sound settings.
- Allow the user to test the microphone before dictating.

When the selected microphone disappears:

1. Attempt to use the current Windows default input device.
2. Notify the user about the switch.
3. Do not silently record from an unexpected device if microphone fallback is disabled.

---

## P0.8 Reliable Text Insertion

Implement a robust insertion pipeline.

### Target Capture

When recording begins, capture:

```text
Process ID
Executable name
Window handle
Window title
Focused UI Automation element, where available
Control type
Whether the process is elevated
Browser identity, where applicable
Timestamp
```

Do not retain an unsafe direct reference to a UI element indefinitely. Revalidate it before insertion.

### Insertion Order

Attempt insertion in this order:

1. Restore the original target window.
2. Verify that the target is still valid.
3. Use a supported direct value or text pattern only when it is safe.
4. Otherwise, use clipboard plus simulated paste.
5. Use `SendInput` as a final fallback.
6. If insertion still fails, preserve the result on the clipboard.

### Clipboard Behavior

Before changing the clipboard:

- Capture the current clipboard data where feasible.
- Retry clipboard access when another process temporarily owns it.
- Put the dictated text on the clipboard.
- Paste it.
- Restore the original clipboard after a short configurable delay when safe.

If insertion fails:

- Keep the dictated text on the clipboard.
- Do not restore the old clipboard over the only recoverable result.
- Show a clear recovery message.

### Elevated Applications

Detect when the target application is running at a higher integrity level.

When input injection is blocked:

- Explain that Windows prevented insertion into an elevated application.
- Copy the transcript to the clipboard.
- Provide an optional setting to run the dictation app as administrator, with an appropriate warning.

### Terminal Handling

For terminal applications:

- Prefer clipboard paste.
- Avoid character-by-character insertion for long transcripts.
- Make terminal paste behavior configurable.
- Never automatically press Enter after insertion in this version.

---

## P0.9 Local Transcript History

Store every completed or recoverable dictation locally.

For each history item, store:

```text
ID
Creation timestamp
Recording start and end time
Duration
Raw ASR transcript
Cleaned transcript
Audio file path
Target application
Target executable
Detected website/domain, when available
Detected output category
Selected style profile
ASR model name
Cleanup model name
ASR duration
Cleanup duration
Insertion duration
Total end-to-end duration
Insertion method
Success or failure state
Error code
Retry count
```

History interface requirements:

- Group entries by day.
- Search raw and cleaned text.
- Copy cleaned text.
- Copy raw text.
- Paste the cleaned transcript again.
- Retry ASR from the saved recording.
- Re-run S1-mini cleanup without rerunning ASR.
- Play the recording.
- Export the recording.
- Delete one entry.
- Delete all entries.
- Open the audio file location.
- Filter failed entries.
- Filter by application.
- Configure automatic retention.

Default retention should be privacy-conscious and clearly documented.

Do not store microphone audio indefinitely without giving the user deletion controls.

---

## P0.10 Failure Recovery

Treat recovery as a core feature rather than an afterthought.

### Incremental Audio Safety

While recording:

- Write audio to a temporary local file incrementally.
- Do not keep the only copy of a long recording in RAM.
- Finalize or repair the file after stopping.
- Mark temporary recordings with a recoverable session ID.

### Automatic Retry

When a transient ASR error occurs:

- Retry automatically once.
- Reuse the saved audio.
- Do not require the user to dictate again.
- Avoid infinite retry loops.

### Manual Retry

Expose retry from:

- Error notification.
- History.
- Tray menu.

Allow:

```text
Retry ASR
Retry cleanup only
Retry insertion
Copy raw transcript
Copy cleaned transcript
```

### Crash Recovery

On application startup:

- Look for unfinished recording sessions.
- Validate the temporary audio files.
- Offer to recover or delete them.
- Never insert recovered text automatically without an explicit user action.

### Error Categories

Distinguish at least:

```text
Microphone initialization failure
Microphone disconnected
No audio received
No speech detected
ASR initialization failure
ASR inference failure
Cleanup model initialization failure
Cleanup inference failure
Target application disappeared
Focus restoration failure
Clipboard failure
Input injection blocked
Database failure
Audio write failure
```

Use typed errors rather than parsing exception strings in the UI.

---

# 7. P0 Transcript Intelligence

The S1-mini cleanup stage must implement all behavior in this section.

## P0.11 Filler-Word Removal

Remove unintentional speech disfluencies such as:

```text
um
uh
erm
repeated articles
accidental duplicated words
abandoned sentence beginnings
unnecessary conversational fillers
```

Do not aggressively remove words that materially affect meaning.

Example:

```text
Raw:
“Um, can you, can you send me the updated, uh, updated spreadsheet?”

Clean:
“Can you send me the updated spreadsheet?”
```

---

## P0.12 Backtracking and Self-Correction

Use the complete utterance to preserve the speaker’s final intention.

Recognize correction patterns including:

```text
actually
no, sorry
scratch that
I mean
make that
or rather
let me correct that
```

Also handle corrections made by simply restating a value.

Examples:

```text
Raw:
“Let’s meet at two—actually, make that three.”

Clean:
“Let’s meet at 3.”
```

```text
Raw:
“Send it Friday. No, sorry, Thursday.”

Clean:
“Send it Thursday.”
```

```text
Raw:
“The total is five hundred, six hundred and twenty.”

Clean:
“The total is 620.”
```

The cleanup stage must not include both the rejected and corrected versions.

---

## P0.13 Automatic Punctuation and Capitalization

Generate readable written text from natural speech.

Support:

- Sentence boundaries.
- Capitalization.
- Commas.
- Periods.
- Question marks.
- Exclamation marks when clearly intended.
- Apostrophes and contractions.
- Paragraph boundaries for longer dictations.

Also support spoken punctuation commands:

```text
comma
period
full stop
question mark
exclamation mark
colon
semicolon
open parenthesis
close parenthesis
new line
new paragraph
slash
backslash
underscore
hyphen
at sign
```

Spoken formatting commands must be removed from the final text after being applied.

Do not insert punctuation words literally unless the context clearly indicates that the user meant the word itself.

---

## P0.14 Number and Text Normalization

Normalize spoken expressions appropriately.

Support:

```text
Cardinal numbers
Ordinal numbers
Dates
Times
Currencies
Percentages
Phone numbers
Email addresses
Web addresses
Common abbreviations
```

Examples:

```text
“twenty five percent”
→ “25%”
```

```text
“August twenty first twenty twenty six”
→ “August 21, 2026”
```

```text
“five thirty p m”
→ “5:30 PM”
```

```text
“alex at example dot com”
→ “alex@example.com”
```

```text
“one hundred and twenty dollars”
→ “$120”
```

Do not apply number conversion where it would damage identifiers, names, or intentional prose.

---

## P0.15 Automatic Structure

Format longer dictation into an appropriate structure.

Support:

- Paragraphs.
- Numbered lists.
- Bulleted lists.
- Email-like formatting where the application style is classified as email.
- Clear separation between greeting, body, and sign-off.
- Preservation of short prose as prose.

Examples:

```text
Raw:
“There are three things we need to do. First update the proposal. Second call the client. Third schedule the review.”

Clean:
“There are three things we need to do:

1. Update the proposal.
2. Call the client.
3. Schedule the review.”
```

Do not create a list merely because a sentence contains several commas.

---

## P0.16 Quiet-Speech and Whisper Usability

The application must remain usable when the user speaks quietly.

Implement:

- Real-time input-level display.
- Configurable minimum input-level warning.
- A noise-floor calibration option.
- Conservative voice activity detection.
- Prevention of premature session termination during quiet pauses.
- Clear notification when the captured audio is too quiet.
- Microphone gain guidance in the settings UI.

Do not use an aggressive noise gate that removes whispered speech.

Save raw audio for failed quiet-speech sessions so the ASR can be retried after settings changes.

---

# 8. Included P1 Feature: Active Application and Website Detection

This is the only P1 capability required in the current release.

The application must detect the active application and, where possible, the active browser website at the beginning of each recording.

Do not implement screenshot-based context detection.

Do not upload application or website information anywhere.

## 8.1 Application Detection

Collect:

```text
Foreground window handle
Process ID
Executable name
Application display name
Window title
UI Automation control type
```

Normalize known executables, including:

```text
chrome.exe
msedge.exe
firefox.exe
outlook.exe
olk.exe
winword.exe
notepad.exe
slack.exe
teams.exe
discord.exe
whatsapp.exe
telegram.exe
notion.exe
code.exe
cursor.exe
windsurf.exe
WindowsTerminal.exe
```

Detection must not fail the recording. When detection fails, use the default style.

---

## 8.2 Browser Website Detection

For supported browsers, attempt to detect the current URL or domain using Windows UI Automation.

Initial browser targets:

```text
Google Chrome
Microsoft Edge
Mozilla Firefox
```

Preferred order:

1. Locate the browser address-bar control through UI Automation.
2. Read its value without moving focus.
3. Parse and normalize the hostname.
4. Discard paths and query strings unless they are needed for classification.
5. If URL retrieval fails, use the window title and browser process as weaker signals.
6. Fall back to the generic browser category.

Never:

- Read page contents.
- Capture screenshots.
- Inspect password fields.
- Store complete URLs containing private paths or query parameters by default.
- Interrupt the user by focusing the address bar.

Store only the normalized domain in history unless the user explicitly enables full-URL storage.

---

# 9. Included P1 Feature: Output-Style Classification

Classify each dictation into an output category using:

```text
Executable name
Application name
Normalized browser domain
Window title
Control type
User-defined overrides
```

Required categories:

```csharp
public enum OutputContextCategory
{
    Email,
    WorkMessaging,
    PersonalMessaging,
    Document,
    AiChat,
    CodeEditor,
    Terminal,
    General
}
```

The classification system must be deterministic and rule-based in the first version.

Do not use another language model merely to classify the application.

---

## 9.1 Default Classification Rules

### Email

Applications and websites such as:

```text
Microsoft Outlook
Gmail
Outlook on the web
Fastmail
Proton Mail
Other configured email clients
```

Default output behavior:

```text
Tone: semi-formal
Structure: email-aware
Complete sentences: yes
Capitalization: standard
Paragraphs: enabled
Lists: enabled when appropriate
Greeting/sign-off formatting: enabled
```

### Work Messaging

Applications and websites such as:

```text
Slack
Microsoft Teams
Google Chat
Mattermost
Workplace chat tools configured by the user
```

Default output behavior:

```text
Tone: semi-casual
Structure: concise prose
Complete sentences: preferred
Capitalization: standard
Paragraphs: short
Lists: enabled when clearly enumerated
```

### Personal Messaging

Applications and websites such as:

```text
WhatsApp
Telegram
Signal
Discord when configured as personal
Messenger
Personal chat tools configured by the user
```

Default output behavior:

```text
Tone: casual
Structure: conversational prose
Capitalization: standard but relaxed
Paragraphs: short
Lists: normally disabled
```

### Document

Applications and websites such as:

```text
Microsoft Word
Notepad
Notion
Google Docs
Obsidian
OneNote
Text editors
```

Default output behavior:

```text
Tone: neutral
Structure: prose
Complete sentences: yes
Paragraphs: enabled
Lists: enabled when appropriate
```

### AI Chat

Applications and websites such as:

```text
ChatGPT
Claude
Gemini
Perplexity
Copilot
Other configured AI assistants
```

Default output behavior:

```text
Tone: neutral
Structure: preserve the user’s requested content
Complete sentences: yes
Paragraphs: enabled
Lists: enabled when explicitly dictated
Do not add an email greeting or sign-off
```

The cleanup model must not answer the dictated prompt. It must only clean the words the user spoke.

### Code Editor

Applications such as:

```text
Visual Studio Code
Cursor
Windsurf
Visual Studio
JetBrains IDEs
```

Default output behavior:

```text
Tone: neutral
Structure: minimally transformed prose
Preserve technical tokens where possible
Avoid decorative punctuation
Avoid converting technical identifiers unnecessarily
Do not automatically create an email structure
```

Full code-symbol awareness is out of scope for this release.

### Terminal

Applications such as:

```text
Windows Terminal
PowerShell
Command Prompt
Git Bash
WSL terminal windows
```

Default output behavior:

```text
Tone: raw
Structure: minimal
Avoid smart quotes
Avoid rich punctuation
Avoid automatic list formatting
Prefer clipboard paste
Never automatically press Enter
```

### General

Fallback behavior:

```text
Tone: neutral
Structure: prose
Capitalization: standard
Punctuation: standard
Lists: enabled only when clearly dictated
```

---

## 9.2 Classification Priority

Use this priority order:

```text
1. User override for a specific domain.
2. User override for a specific executable.
3. Known website-domain rule.
4. Known application rule.
5. Control-type hint.
6. Generic browser rule.
7. General fallback.
```

A domain-specific rule should override a browser-level rule.

For example:

```text
chrome.exe + mail.google.com
→ Email

chrome.exe + docs.google.com
→ Document

chrome.exe + chatgpt.com
→ AiChat

chrome.exe + slack.com
→ WorkMessaging
```

---

## 9.3 Style Profiles

Represent output style as a structured object instead of scattered Boolean settings.

Example:

```csharp
public sealed record TranscriptStyle(
    OutputContextCategory Category,
    ToneProfile Tone,
    StructureProfile Structure,
    bool UseStandardCapitalization,
    bool EnableParagraphs,
    bool EnableLists,
    bool EnableEmailFormatting,
    bool PreserveTechnicalTokens,
    bool UseSmartPunctuation);
```

Pass this object into the S1-mini prompt adapter.

The adapter must map application categories to the exact style controls supported by S1-mini.

Do not invent unsupported control tokens. If S1-mini does not support a requested setting directly, enforce only what can be achieved reliably through its documented input format.

---

## 9.4 User Overrides

Add settings that allow users to:

- Change the default category for an executable.
- Change the default category for a website domain.
- Disable website detection.
- Disable style classification.
- Select a universal default style.
- Reset rules to defaults.
- View the detected application, domain, category, and style before testing.
- Mark Discord or another ambiguous application as work or personal.
- Set an application to raw/minimal formatting.

Store overrides locally in SQLite or a local settings file.

---

# 10. End-to-End Processing Pipeline

Implement the following pipeline.

## Recording Start

```text
1. Receive push-to-talk or hands-free start action.
2. Validate that no recording is active.
3. Capture the active window and focused element.
4. Detect executable and application.
5. Detect browser domain when applicable.
6. Classify the output context.
7. Resolve the transcript style.
8. Create a recoverable session record.
9. Open the temporary audio file.
10. Start microphone capture.
11. Start the Nemotron ASR session.
12. Display the listening overlay.
```

## During Recording

```text
1. Receive microphone audio.
2. Convert to 16 kHz, 16-bit, mono PCM when required.
3. Append audio to the recovery file.
4. Send audio chunks to Nemotron.
5. Update the waveform, microphone level, and duration.
6. Keep partial ASR results internal.
```

## Recording Stop

```text
1. Stop accepting new microphone audio.
2. Flush the resampler.
3. Finalize the audio file.
4. Complete the ASR session.
5. Validate the raw transcript.
6. Send the raw transcript and selected style to S1-mini.
7. Validate the cleaned result.
8. Restore and verify the target application.
9. Insert the cleaned text.
10. Save all metadata to history.
11. Show success or recovery status.
12. Return to Idle.
```

## Cleanup Failure Fallback

If S1-mini fails but ASR succeeds:

```text
1. Preserve the raw transcript.
2. Attempt one cleanup retry if the failure is transient.
3. If retry fails, offer to insert or copy the raw transcript.
4. Save the item to history as “ASR succeeded, cleanup failed.”
```

Never lose a usable raw transcript because the cleanup model failed.

---

# 11. Model Initialization and Resource Management

Models should initialize lazily or during application startup based on a setting.

Provide clear model states:

```text
Not installed
Downloading
Verifying
Loading
Ready
Failed
Unsupported hardware
```

Requirements:

- Show model download size before downloading.
- Verify downloaded files with checksums where available.
- Allow the user to choose the model storage directory.
- Detect insufficient disk space.
- Avoid loading duplicate model instances.
- Release sessions after use without repeatedly unloading the entire model.
- Keep the UI responsive while models load.
- Provide a model diagnostics page.
- Log actual model initialization and inference times.
- Do not silently fall back to a cloud service.

Create backend interfaces so future models can be added without rewriting the application:

```csharp
public interface IAsrBackend
{
    string BackendId { get; }
    string DisplayName { get; }
    Task<BackendAvailability> CheckAvailabilityAsync(
        CancellationToken cancellationToken);
}
```

```csharp
public interface ICleanupBackend
{
    string BackendId { get; }
    string DisplayName { get; }
    Task<BackendAvailability> CheckAvailabilityAsync(
        CancellationToken cancellationToken);
}
```

Do not implement the future Cohere or Qwen backends in this release.

---

# 12. Settings UI

Create a settings window with these sections:

```text
General
Shortcuts
Microphone
Models
Appearance
Application Styles
Website Styles
History and Privacy
Diagnostics
About
```

## General

Include:

- Start with Windows.
- Minimize to tray.
- Show startup notification.
- Default push-to-talk behavior.
- Enable hands-free double-tap.
- Double-tap interval.
- Play start and stop sounds.

## Shortcuts

Include:

- Shortcut editor.
- Mouse-button editor.
- Conflict detection.
- Test binding button.
- Reset defaults.

## Microphone

Include:

- Device selector.
- Follow Windows default toggle.
- Input level.
- Test recording.
- Noise-floor calibration.
- Quiet-audio warning threshold.

## Models

Include:

- ASR model status.
- Cleanup model status.
- Download controls.
- Model path.
- Load/unload controls.
- Initialization test.
- Sample transcription test.
- Actual detected execution provider.

## Application and Website Styles

Include:

- Detected application tester.
- Detected website tester.
- Rule list.
- Add override.
- Edit override.
- Remove override.
- Reset defaults.
- Universal fallback style.

## History and Privacy

Include:

- Save audio toggle.
- Audio retention period.
- Transcript retention period.
- Clear history.
- Clear recordings.
- Open data directory.
- Disable website detection.
- Store normalized domains toggle.
- Store full URLs toggle, off by default.

## Diagnostics

Include:

- Application version.
- Windows version.
- CPU, GPU, RAM, and accelerator information.
- Microphone details.
- ASR initialization state.
- Cleanup model initialization state.
- Recent structured logs.
- Export diagnostics package.

Do not include transcript or audio content in exported diagnostics unless the user explicitly opts in.

---

# 13. Privacy and Security Requirements

The application is local-first.

Mandatory rules:

- No cloud inference.
- No analytics by default.
- No transcript telemetry.
- No recording telemetry.
- No background recording.
- Recording begins only after an explicit shortcut or button action.
- Display a clear recording indicator whenever the microphone is active.
- Do not read complete webpage content.
- Do not inspect password fields.
- Do not persist private URL paths or query strings by default.
- Encrypting the local database is optional for the first release, but design the repository so encryption can be added.
- Store data under the user’s application-data directory.
- Use restrictive file permissions where practical.
- Provide complete deletion controls.
- Never send crash dumps containing transcripts or recordings automatically.

When the focused UI Automation control reports that it is a password or protected field:

- Do not record surrounding text.
- Do not attempt direct-value insertion.
- Warn the user or use clipboard-only behavior according to settings.

---

# 14. Performance and Responsiveness

The application must remain responsive during:

- Audio capture.
- Model initialization.
- ASR inference.
- Cleanup inference.
- Database operations.
- Audio playback.
- History search.

Do not perform blocking inference on the UI thread.

Instrument these timings:

```text
Hotkey-to-overlay latency
Microphone startup latency
ASR finalization latency
Cleanup latency
Focus restoration latency
Insertion latency
Total release-to-insertion latency
```

Initial performance targets on a modern supported Windows laptop:

```text
Overlay visible within 150 ms of recording start.
No lost audio at the beginning of a recording.
No UI freezes longer than 100 ms.
Insertion begins as soon as the cleaned transcript is available.
Application memory remains stable across repeated sessions.
```

Do not fake performance results. Include actual benchmark output in the diagnostics page or development documentation.

---

# 15. Testing Requirements

Create automated and manual tests.

## Unit Tests

Test:

- Recording state transitions.
- Hotkey repeat suppression.
- Hands-free double-tap detection.
- Application classification.
- Website-domain classification.
- Override priority.
- Style-profile resolution.
- Clipboard restoration decisions.
- Retry policies.
- History retention calculations.
- Number and date normalization test inputs.
- Cleanup result validation.
- Temporary recording recovery.
- Error categorization.

## Integration Tests

Use fake implementations of:

```text
IAsrService
ITranscriptCleaner
IAudioCaptureService
ITextInsertionService
IActiveTargetTracker
```

Test complete flows:

```text
Successful push-to-talk
Successful hands-free recording
Cancellation
ASR failure followed by successful retry
Cleanup failure with raw-transcript fallback
Insertion failure with clipboard recovery
Target window closed before insertion
Application style override
Website style override
Crash recovery
```

## Manual Compatibility Matrix

Test at minimum:

| Target           | Required scenario                          |
| ---------------- | ------------------------------------------ |
| Notepad          | Basic insertion and paragraph dictation    |
| Word             | Multiline insertion                        |
| Outlook          | Email-style formatting                     |
| Gmail in Chrome  | Website detection and email style          |
| Slack            | Work-message style                         |
| WhatsApp Web     | Personal-message style                     |
| Notion           | Document style                             |
| ChatGPT          | AI-chat style without answering the prompt |
| VS Code          | Code-editor style                          |
| Windows Terminal | Clipboard paste without Enter              |

## Transcript Behavior Tests

Create repeatable audio or text fixtures for:

```text
Filler removal
Repeated words
Self-correction
Dates
Times
Currency
Percentages
Email addresses
Spoken punctuation
New lines
New paragraphs
Numbered lists
Quiet speech
No speech
Background noise
```

Example acceptance cases:

```text
Input:
“Um send it on Friday, no sorry, Thursday at five thirty p m.”

Expected intent:
“Send it on Thursday at 5:30 PM.”
```

```text
Input:
“There are three tasks. First update the document. Second email Priya. Third schedule the meeting.”

Expected intent:
A correctly punctuated numbered list.
```

Do not require exact string equality where several punctuation choices are valid. Test semantic intent and key transformations.

---

# 16. Logging and Diagnostics

Use structured logs.

Every recording session should have a correlation ID.

Log:

```text
State transitions
Shortcut events
Audio device changes
ASR session lifecycle
Cleanup session lifecycle
Target capture and restoration
Classification result
Insertion strategy
Retry attempts
Typed errors
Performance timings
```

Do not log:

```text
Raw audio
Raw transcript
Cleaned transcript
Clipboard contents
Complete private URLs
Password-field data
```

Allow sensitive development logging only behind an explicit developer setting that is off by default.

Rotate logs and enforce a maximum storage size.

---

# 17. Packaging and Installation

Produce:

- A self-contained Windows x64 build.
- An installer or MSIX package.
- A portable development build where practical.
- Start-menu shortcut.
- Optional start-with-Windows registration.
- Clean uninstallation.
- Preservation or deletion choice for local history during uninstall.

The installer must not silently install unrelated software.

Document any Foundry Local prerequisite separately and provide a readiness check inside the application.

---

# 18. Deliverables

Produce all of the following:

1. Complete source repository.
2. Buildable Visual Studio solution.
3. Working Windows tray application.
4. Global push-to-talk.
5. Hands-free recording.
6. Nemotron local ASR integration.
7. S1-mini local cleanup integration.
8. Application and website detection.
9. Output-style classification.
10. Reliable insertion and clipboard recovery.
11. Local history with audio and retry.
12. Settings interface.
13. Automated tests.
14. Manual compatibility checklist.
15. Setup and model-installation documentation.
16. Architecture document.
17. Troubleshooting document.
18. Known-limitations document.
19. Packaging script.
20. Sample configuration and classification rules.

The README must include:

```text
System requirements
Required Windows version
Build instructions
Run instructions
Model installation
First-run setup
Microphone setup
Shortcut configuration
Privacy behavior
Data storage locations
Troubleshooting
How to run tests
```

---

# 19. Explicitly Deferred Features

Do not implement these features in the current release:

```text
Personal dictionary
Learning from user corrections
Voice-triggered snippets
General Command Mode
Selected-text rewriting
Custom transforms
Automatic post-dictation transforms
Diff viewer
Multilingual normalization
Hindi or Hinglish cleanup
Scratchpad
Usage statistics
IDE variable-name awareness
Project symbol indexing
Cursor or Windsurf file tagging
Meeting recording
Meeting transcription
Notetaker
Team features
Cloud synchronization
Mobile application
Browser extension
Cloud account system
Billing
General-purpose AI assistant
Automatic “press Enter” voice command
```

Create clean extension points where appropriate, but do not spend implementation time building deferred functionality.

In particular:

- Do not add Qwen or another general LLM yet.
- Do not add Cohere Transcribe yet.
- Do not implement cloud fallbacks.
- Do not turn the product into a meeting recorder.
- Do not read webpage content for context.
- Do not build a browser extension in this phase.

---

# 20. Development Approach

Work in vertical slices.

## Milestone 1: Basic Local Dictation — ✅ Complete

Implement:

```text
✅ Tray application
✅ Global push-to-talk
✅ Audio capture
✅ Fake ASR
✅ Fake cleanup
✅ Clipboard insertion
✅ Basic overlay
```

Verify the complete state machine before integrating models.

## Milestone 2: Real ASR — ✅ Complete

Implement:

```text
✅ Foundry Local readiness detection
✅ Nemotron initialization
✅ Streaming audio input
✅ Final result
✅ Retry from saved audio
```

## Milestone 3: Real Cleanup — ✅ Complete

Implement:

```text
✅ llama.cpp integration
✅ S1-mini loading
✅ Official prompt adapter
✅ Deterministic inference
✅ Cleanup validation
✅ Raw-transcript fallback
```

## Milestone 4: Reliable Windows Integration — ✅ Complete

Implement:

```text
✅ Target capture
✅ Focus restoration
✅ UI Automation insertion
✅ Clipboard paste
✅ SendInput fallback
✅ Elevated-window detection
✅ Terminal handling
```

## Milestone 5: Application-Aware Styling — ✅ Complete

Implement:

```text
✅ Executable detection
✅ Browser-domain detection
✅ Rule-based classification
✅ Style-profile mapping
✅ User overrides
✅ Classification diagnostics
```

## Milestone 6: History and Recovery — ✅ Complete

Implement:

```text
✅ SQLite persistence
✅ Incremental audio files
✅ History UI
✅ Retry controls
✅ Crash recovery
✅ Retention settings
```

## Milestone 7: Hardening — ✅ Complete

Complete:

```text
✅ Automated tests
✅ Manual compatibility matrix
✅ Performance measurements
✅ Installer
✅ Documentation
✅ Accessibility review
✅ Privacy review
```

Each milestone must leave the application in a runnable state.

Do not leave the final repository dependent on fake model services.

---

# 21. Coding Standards

Use:

- Nullable reference types.
- Async APIs for I/O and inference.
- Cancellation tokens.
- Immutable records for request and result objects where appropriate.
- Typed configuration.
- Typed error results.
- Structured logging.
- Dependency injection.
- Clear interfaces around native and model integrations.
- XML documentation for public interfaces.
- Small focused classes.
- Unit-testable state transitions.

Avoid:

- Large WPF code-behind files.
- Global mutable state.
- Blocking `.Result` or `.Wait()` calls.
- Swallowing exceptions.
- Unbounded queues.
- Recording audio entirely in memory.
- Hard-coded model paths.
- Hard-coded user directories.
- Silent cloud fallbacks.
- Storing sensitive transcript contents in logs.
- Treating every application as a generic clipboard target.

---

# 22. Completion Criteria

The release is complete only when a user can perform this scenario:

1. Install and launch the app on Windows 11.

2. Select or confirm a microphone.

3. Install or locate the local Nemotron and S1-mini models.

4. Focus a Gmail compose field in Chrome.

5. Hold the push-to-talk shortcut.

6. Say:

   ```text
   “Hi Maya comma new paragraph um I reviewed the proposal and I think we should send it Friday no sorry Thursday afternoon period new paragraph thanks comma Arjun”
   ```

7. Release the shortcut.

8. Receive a clean, email-formatted result similar to:

   ```text
   Hi Maya,

   I reviewed the proposal, and I think we should send it Thursday afternoon.

   Thanks,
   Arjun
   ```

9. See the text inserted into Gmail.

10. Open Slack and dictate a shorter message.

11. See the application classify Slack as work messaging and use a less formal style.

12. Open Windows Terminal and dictate text.

13. See minimal formatting, clipboard-based insertion, and no automatic Enter key.

14. Open history and find the audio, raw transcript, cleaned transcript, application category, and timing information.

15. Retry the transcript from the saved audio without recording again.

16. Disconnect the microphone and receive a clear recoverable error.

17. Restart after an interrupted recording and receive a recovery option.

Prioritize a dependable end-to-end experience over adding extra features. The defining qualities of this release are local execution, low friction, faithful cleanup, reliable insertion, recoverability, and application-aware formatting.
