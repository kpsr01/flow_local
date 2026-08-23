# Windows 11 Manual Compatibility Checklist

Status values: `UNVERIFIED`, `PASS`, `FAIL`, `BLOCKED`. All results below start `UNVERIFIED`; replace them only after executing the procedure and attaching evidence.

## Test record and prerequisites

- Tester / UTC date: `UNVERIFIED`
- Windows edition, version, OS build, architecture (`winver`; `systeminfo`): `UNVERIFIED`
- FlowLocal build/version and configuration: `UNVERIFIED`
- Target application/browser versions: `UNVERIFIED`
- Microphone/model/runtime details: `UNVERIFIED`
- Evidence folder: `UNVERIFIED`

Prerequisites:

1. Use Windows 11 x64 with FlowLocal, its local ASR model/runtime, and the cleanup model installed and initialized.
2. Configure a working microphone and the normal push-to-talk shortcut. Start FlowLocal and wait until it reports ready.
3. Install/sign in to every target below. Use Chrome for Gmail and the browser versions of WhatsApp, Notion, and ChatGPT. For VS Code and Windows Terminal, open a disposable text file and a harmless shell prompt respectively.
4. Enable Windows Game Bar recording (`Win+Alt+R`) or another screen recorder that includes a visible clock. Never capture secrets or private account content.
5. For each row, start a fresh target document/conversation, focus the named field, start recording, hold the shortcut while speaking the exact script, release it, wait for completion, and save a screenshot or recording plus the matching FlowLocal History entry. Record the evidence path and observed text; do not infer a pass from classification alone.

Common checks for every row:

- The overlay appears without stealing focus; the target remains the intended insertion destination.
- Dictation completes without UI hangs, duplicate insertion, unintended submission, or pasted clipboard residue.
- The inserted text faithfully reflects the script and the requested target-specific style.
- FlowLocal History records the target, detected website when applicable, output style, transcript, insertion method, and any error.

## Required matrix

| ID | Target and field | Exact script / procedure | Expected behavior | Status | Observed text / evidence |
| --- | --- | --- | --- | --- | --- |
| C01 | Notepad, blank document | Say: “Project update new paragraph The design review is Thursday at five thirty PM.” | Basic insertion produces two paragraphs, with sensible punctuation/capitalization, and no duplicate text. | `UNVERIFIED` | `UNVERIFIED` |
| C02 | Word, blank document | Say: “Release notes new line First item colon microphone setup new line Second item colon local models.” | Multiline insertion preserves the requested line breaks in Word and does not replace existing content. | `UNVERIFIED` | `UNVERIFIED` |
| C03 | Outlook, new message body | Say: “Hi Morgan new paragraph The draft is ready for review period Please send comments by Friday period new paragraph Thanks comma Alex.” | Email-style formatting preserves greeting/body/sign-off paragraphs; it inserts into the body and does not send. | `UNVERIFIED` | `UNVERIFIED` |
| C04 | Gmail in Chrome, Compose body | Say the C03 script. Before dictation, open FlowLocal diagnostics and run **Test current target**; capture the detected domain/style. | Domain is detected as Gmail/Google mail, email style is selected, formatted text enters the body, and no send action occurs. | `UNVERIFIED` | `UNVERIFIED` |
| C05 | Slack, unsent message composer | Say: “Quick update colon the Windows build is ready comma and I will share the results this afternoon.” | Work-message style is concise and conversational; text remains an unsent draft and Enter is not injected. | `UNVERIFIED` | `UNVERIFIED` |
| C06 | WhatsApp Web, unsent message composer | Say: “Hey comma I am leaving now period See you in twenty minutes exclamation mark.” | Personal-message style is natural; text remains unsent and Enter is not injected. | `UNVERIFIED` | `UNVERIFIED` |
| C07 | Notion, empty page body | Say: “Compatibility notes new paragraph The app works offline after model installation period new paragraph Next step colon verify insertion targets.” | Document style uses clear prose and requested paragraphs in the page body. | `UNVERIFIED` | `UNVERIFIED` |
| C08 | ChatGPT, empty prompt composer | Say: “Explain how our release checklist is organized comma and include the performance measurements we collected.” | AI-chat style cleans only the dictated request; it must not answer, expand, or execute the prompt, and must not submit it. | `UNVERIFIED` | `UNVERIFIED` |
| C09 | VS Code, disposable text file | Say: “public static void main open parenthesis close parenthesis new line open brace new line console dot write line open parenthesis quote hello quote close parenthesis semicolon new line close brace.” | Code-editor style preserves code-like punctuation/line structure as faithfully as cleanup permits; insertion stays in the editor. Record exact output rather than correcting it. | `UNVERIFIED` | `UNVERIFIED` |
| C10 | Windows Terminal, harmless prompt | Say: “echo compatibility check.” | Text is inserted by clipboard paste/fallback but **Enter is not sent**: the command remains visible and unexecuted. Clear it manually after evidence capture. | `UNVERIFIED` | `UNVERIFIED` |

## Completion

- Every row has a non-`UNVERIFIED` status and an evidence path: `UNVERIFIED`
- Failures include exact reproduction steps, observed text, insertion method/error, target version, and evidence: `UNVERIFIED`
- Overall Windows 11 compatibility result: `UNVERIFIED`
