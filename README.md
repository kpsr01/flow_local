# FlowLocal

FlowLocal is a Windows 11 x64 WPF dictation app. Hold the global shortcut, speak, and release to insert **only your recognized speech** into the active target. Recognition, speaker diarization, and voice matching run locally on CPU. There is no transcript cleanup or language-model rewrite; the inserted text is the raw Multitalker transcript.

## Requirements

- Windows 11 x64 and a working microphone with desktop-app permission.
- .NET 9 SDK and Rust/Cargo to build from source. Packaged releases include the .NET runtime and the native worker.
- Internet access once to download the speech and speaker models. Runtime inference stays on the PC. The app downloads models at first initialization into `%LOCALAPPDATA%\FlowLocal\Models` (set `FLOWLOCAL_MODELS_DIR` to use another directory).
- Enough free space and RAM for the INT8 Multitalker Parakeet encoder/decoder, Sortformer v2.1, and ECAPA speaker verifier ONNX models.

## Enroll your voice

Open **System status → Your voice → Enroll my voice**. Speak alone near the microphone for the 11-second recording; at least eight seconds of sufficiently audible speech are required. The 192-dimensional voice embedding is stored at `%LOCALAPPDATA%\FlowLocal\user-embedding.json`; it can be replaced by enrolling again. Audio for enrollment is not uploaded. Dictation refuses to start without enrollment.

During dictation, [Sortformer v2.1](https://huggingface.co/altunenes/parakeet-rs) identifies up to four simultaneous speaker tracks, and [Multitalker Parakeet Streaming INT8](https://huggingface.co/smcleod/multitalker-parakeet-streaming-0.6b-v1-onnx-int8) transcribes tracks continuously via [parakeet-rs](https://github.com/altunenes/parakeet-rs). A local [ECAPA-TDNN speaker embedding](https://huggingface.co/vedk00/ecapa-voxceleb-speaker-embedding-onnx) compares clean non-overlapping speech against the enrollment. Only a verified speaker channel can contribute text; ambiguous/overlapping speech is not used to establish identity. If no confident match occurs, nothing is inserted. This is speaker filtering, not a security-grade biometric authentication system; noisy, short, or changing voices can be missed or falsely matched.

## Build and run

From the repository root in PowerShell:

```powershell
dotnet restore .\FlowLocal.slnx
dotnet build .\FlowLocal.slnx -c Release
dotnet test .\FlowLocal.slnx -c Release
dotnet run --project .\src\FlowLocal.App\FlowLocal.App.csproj -c Release
```

Builds require Rust/Cargo on `PATH` and compile the native worker. To package a self-contained release, install Inno Setup 6 and run `./pack.ps1 -Configuration Release -Version X.Y.Z`. Use `-PortableOnly` to publish without Inno Setup. The app and native worker reside in the output together; first-run model acquisition happens in the worker, not in the installer.

## History and privacy

Recordings and raw transcripts remain in `%LOCALAPPDATA%\FlowLocal`; History supports copying or pasting the raw text, retrying recognition from saved audio, and deleting entries. Older rows can still display prior cleaned text, but new dictations do not create it. The app never automatically presses Enter in a target. On uninstall, the local history, models, and voice embedding are removed.

The app checks GitHub Releases for signed update metadata and verifies the downloaded setup SHA-256 before launching it. First-run model downloads and update checks require network access; speech and transcripts do not leave the local worker.

For manual coverage and measured performance, see [compatibility](docs/manual-compatibility.md), [performance](docs/performance-measurements.md), and [known limitations](docs/known-limitations.md). These are procedures, not completed benchmark claims.
