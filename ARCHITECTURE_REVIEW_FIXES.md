# Architecture Review Fixes

Date: 2026-06-05

## Scope

This change set implements the architecture review items without adding a full plugin system or full MVVM rewrite. The target is practical scalability from the current 2 information tabs toward many tab-specific information screens, while keeping real-time work off the UI thread.

## Implemented Changes

### UI frame coalescing and graph data contract

- Graph payloads now use a replace-snapshot contract for scope and rate series.
- Legacy frame-local scope arrays were removed from `AnalysisFrame`; consumers use `ScopeSeries` and `RateSeries`.
- `AnalysisWorker` keeps bounded visible-window scope state and publishes replace snapshots.
- Scope snapshot storage is capped by `ScopeSnapshotPointBudget`, with stride computed from sample rate and the visible scope window.
- `MasterAudioBuffer.CopyAnalysisSamples` copies analysis blocks under the buffer lock and bounds each analysis pass to a fixed source snapshot.

### Renderer update path

- ScottPlot plottables are kept and backed by mutable data lists instead of being recreated every frame.
- Series reduction/decimation was extracted into `SeriesDataReducer` for direct tests.
- `AnalysisFrameRouter` calls `ObserveFrame` for every registered tab consumer and only calls expensive `RenderFrame` for the active tab.
- Rate/scope and sound-print rendering are split into `RateScopeRenderer` and `SoundPrintRenderer`.
- `GraphFrameRenderer` is now a small shared-state facade over catalog-created consumers.

### Tab scaling guardrails

- App-side tab metadata lives in `InfoTabCatalog`.
- `InfoTabRegistry` creates tab items, Avalonia controls, renderers, and frame consumers from the catalog.
- XAML no longer contains per-graph `TabItem`, `AvaPlot`, or `Image` declarations.
- Tab switching repaints the selected tab from the latest valid analysis frame immediately.
- `ActiveInfoTabId` no longer depends on hard-coded selected index fallbacks.

### Core analysis hot path

- Ring-buffer sample reads no longer access `Samples` or advance analysis read indexes outside the buffer lock.
- `AnalysisFrame` carries `PendingSamples`, `AnalysisLagSamples`, and `ProcessingElapsedMs` for backpressure visibility.
- Recording uses `QueuedWavStreamWriter`, a bounded background writer with pooled buffers.
- The recording writer no longer disposes the underlying stream while its writer thread is still active.
- `DetectorMetricsEngine` is shared by live analysis and verifier.
- `DetectorMetricsEngine` returns `DetectorResultSnapshot` instead of exposing the mutable reused `TgResult`.

### Session and lifecycle fixes

- Playback/sim completion callbacks carry run tokens and completion reasons.
- Pending UI frames and delayed render dispatches carry a generation token.
- Start enters `Starting` before the first `await`.
- Stop enters `Stopping` first and returns to stopped mode only after workers and the recording writer close cleanly.
- Analysis/audio/playback/sim stop paths use bounded timeout joins from the UI thread.
- Input `DataReady` handlers are session-token gated and detached per worker.
- Window close invalidates the run session and closes the WAV writer.

### Platform boundary

- NAudio-backed live capture and Windows endpoint volume control moved to `TimeGrapher.Platform.WindowsAudio`.
- `TimeGrapher.Core` is free of NAudio and platform project references.
- `TimeGrapher.Core` no longer declares a Windows RID and no longer produces a package lock file because it has no package dependencies.
- `TimeGrapher.App`, `TimeGrapher.Platform.WindowsAudio`, and `TimeGrapher.App.Tests` target `net8.0-windows`.
- Windows audio references are narrowed to `NAudio.WinMM` and `NAudio.Wasapi`.

### WAV validation and CI

- `WavProbe` validates chunk movement, `blockAlign`, `byteRate`, data alignment, non-empty data, and full WAVE_FORMAT_EXTENSIBLE SubFormat GUID.
- `WavProbe`, `WavFileReader`, and playback share the same playback acceptance contract.
- `WavStreamWriter` rejects writes that would overflow RIFF 32-bit size fields.
- `TimeGrapher.Verify --generated --byte-fixtures` covers writer-generated WAVs and byte-built RIFF fixtures independent of `WavStreamWriter`.
- CI includes an Ubuntu core-boundary job for Core/Verify/Core.Tests and a source check against platform dependency text in Core.
- CI runs generated plus byte-built WAV verification as a mandatory gate.
- CI restores the Windows publish RID in locked mode, publishes with `--no-restore`, runs the published app with `--smoke`, and writes a SHA-256 manifest.

## Tests Added

- `TimeGrapher.App.Tests`
  - `InfoTabCatalogTests`
  - `InfoTabRegistryTests`
  - `AnalysisFrameRouterTests`
  - `SeriesDataReducerTests`
- `TimeGrapher.Core.Tests`
  - `AnalysisFrameContractTests`
  - `WavProbeTests`
  - `WavWriterTests`
  - playback completion reason coverage

## Verification

Passed:

```powershell
dotnet restore TimeGrapherNet.sln --locked-mode
dotnet restore src\TimeGrapher.App\TimeGrapher.App.csproj -r win-x64 --locked-mode
dotnet build TimeGrapherNet.sln -c Release --no-restore /p:TreatWarningsAsErrors=true
dotnet test TimeGrapherNet.sln -c Release --no-build --logger "trx;LogFilePrefix=test-results" --results-directory TestResults
dotnet run --project .\src\TimeGrapher.Verify\TimeGrapher.Verify.csproj -c Release --no-build -- --generated --byte-fixtures
dotnet publish src\TimeGrapher.App\TimeGrapher.App.csproj -c Release -r win-x64 --self-contained false --no-restore -o artifacts\TimeGrapher.App-win-x64
.\artifacts\TimeGrapher.App-win-x64\TimeGrapher.App.exe --smoke
```

Generated verifier output confirmed synced detections for:

- `18000BPH_clean_48000Hz_generated.wav`
- `21600BPH_noisy-lowamp_48000Hz_generated.wav`
- `28800BPH_highrate_96000Hz_generated.wav`
- `36000BPH_edge_48000Hz_generated.wav`
- `43200BPH_max-standard-rate_192000Hz_generated.wav`
- `18000BPH_riff-junk_48000Hz_bytefixture.wav`
- `21600BPH_extensible_48000Hz_bytefixture.wav`
- `28800BPH_extensible-list_96000Hz_bytefixture.wav`
