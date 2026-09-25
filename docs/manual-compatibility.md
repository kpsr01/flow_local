# Manual compatibility checklist

Results below are `UNVERIFIED` until tested on actual Windows 11 targets. Record FlowLocal version, CPU, microphone, target app/browser versions, enrollment conditions, and original screen/audio evidence. Do not include secrets in recordings.

## Prerequisites

1. Install FlowLocal, let local ONNX models initialize, and enroll the primary speaker with 11 seconds of clean solo speech in **System status**.
2. Configure a working input device and shortcut; use a disposable writable target.
3. Keep the target in focus at shortcut-down, speak, release, and observe whether the final **raw** recognized text is inserted once. There is no cleanup or app-specific rewrite.
4. For multi-speaker cases, use consented prerecorded voices played in the same room or two volunteers. Record who spoke when, including overlaps; do not infer speaker correctness from grammatical text alone.

| ID | Scenario | Acceptance | Result |
| --- | --- | --- | --- |
| C01 | Notepad, enrolled voice alone | Only the spoken words are inserted once; History raw text matches insertion. | `UNVERIFIED` |
| C02 | Notepad, unregistered voice alone for at least 10 seconds | No text is inserted; no unregistered partial reaches the UI. | `UNVERIFIED` |
| C03 | Enrolled speaker and another person overlap | Only enrolled speaker words appear, with no other person's words. | `UNVERIFIED` |
| C04 | Other speaker starts, then enrolled speaker follows | Other speaker words are excluded; enrolled speech appears after verification. | `UNVERIFIED` |
| C05 | Enrolled speaker starts, then other speaker joins | Earlier user speech remains; joined speech does not leak into inserted text. | `UNVERIFIED` |
| C06 | Silence, short utterance, or uncertain match | No wrong-speaker text is inserted; failure is visible and WAV can be retried. | `UNVERIFIED` |
| C07 | Word/Outlook/Gmail/Slack/WhatsApp/Notion/ChatGPT | Raw transcript is inserted only into the intended writable field; Enter is never sent. | `UNVERIFIED` |
| C08 | VS Code and Windows Terminal | Raw technical text is inserted without model rewriting; shell command is not executed. | `UNVERIFIED` |
| C09 | Protected/elevated/password or replaced target | No unsafe injection; clipboard-only recovery is not reported as inserted. | `UNVERIFIED` |
| C10 | Cancel during capture; retry from saved recording | Cancelled dictation inserts nothing; retry applies current enrolled identity filter. | `UNVERIFIED` |

For each case save the exact observed inserted string, History row, model status, and timestamped evidence. Mark PASS only on direct observation; record omissions and false acceptances as failures. Tests with the same prerecorded voice in different files do not establish different-speaker separation.
