# Data Model View

TimeGrapherNet은 별도 데이터베이스를 사용하지 않는다. 따라서 전통적인 persisted domain element는 WAV 파일이며, 나머지는 실행 중 생성·전달·렌더링되는 도메인 데이터 구조다. 이 다이어그램은 프로젝트가 조작하는 주요 데이터 엔티티와 1:1, 1:n, 집합/집약, 일반화/특수화 관계를 함께 보여준다.

```mermaid
classDiagram
direction TB

class AnalysisRun {
    +ulong SessionId
    +RunMode Mode
}

class AnalysisRunSettings {
    +int SampleRate
    +double LiftAngle
    +bool AutoBph
    +int ManualBph
    +double HpfCutoffHz
    +int SoundImageSize
}

class AudioSource {
    <<abstract>>
    +string SourceKind
}

class LiveAudioSource {
    +double Gain
}

class PlaybackSource {
    +string FilePath
}

class SimSource {
    +double Bph
    +double RateError
    +double BeatError
}

class LiveAudioDevice {
    +int Number
    +string Name
}

class WatchSynthStreamConfig {
    +uint SampleRateHz
    +double Bph
    +double RateErrorSPerDay
    +double BeatErrorMs
    +double WatchAmplitudeDegrees
}

class WavFile {
    +string Path
    +bytes RIFF_WAVE_Data
}

class WavFormatInfo {
    +ushort AudioFormat
    +ushort NumChannels
    +int SampleRate
    +ushort BitsPerSample
    +long DataOffset
    +uint DataSize
}

class WavData {
    +int SampleRate
    +float Samples
}

class MasterAudioBuffer {
    +int Channels
    +int SecondsOfBuffer
    +float Samples
    +ulong TotalSamplesWritten
}

class AnalysisFrame {
    +ulong SessionId
    +ulong SourceId
    +ulong SourceSampleEnd
    +int SampleRate
    +bool InputOverrun
}

class GraphSeriesFrame {
    +string Id
    +double X
    +double Y
    +bool Replace
}

class ScopeMarker {
    <<abstract>>
    +double X
    +uint Color
}

class ScopeVerticalMarker {
    +double Height
}

class ScopeHorizontalMarker {
    +double XLeft
    +double XRight
    +double Length
}

class ScopeTextMarker {
    +string Text
    +MarkerTextAlignment Alignment
}

class WatchMetricsUpdate {
    +double TicRatePoints
    +double TocRatePoints
    +string ResultsText
    +string CMarkerText
    +BeatTimingSample BeatTimingSample
    +AmplitudeSample AmplitudeSample
    +DerivedTimingMeasures DerivedMeasures
}

class BeatTimingSample {
    +ulong BeatNumber
    +double TimeS
    +bool IsTic
    +double RateErrorMs
    +double RateSPerDay
    +double BeatErrorSignedMs
    +int Bph
}

class AmplitudeSample {
    +double TimeS
    +double InstantDeg
    +double PairAverageDeg
}

class DerivedTimingMeasures {
    +double DiffTicTacMs
    +double DiffPeriodMs
    +double AvgPeriodMs
}

class BeatMetricsHistorySnapshot {
    +ulong Version
    +MetricsHistorySeries Rate
    +MetricsHistorySeries Amplitude
    +MetricsHistorySeries BeatError
    +DerivedTimingMeasures Derived
    +StatsSummary RateStats
    +StatsSummary AmplitudeStats
    +int Bph
    +double LatestTimeS
    +WatchPosition ActivePosition
    +PositionSummary Positions
}

class WatchPosition {
    <<enumeration>>
    CH
    CB
    P6H
    P9H
    P3H
    P12H
}

class PositionSummary {
    +WatchPosition Position
    +StatsSummary Rate
    +StatsSummary Amplitude
    +StatsSummary BeatError
}

class StatsSummary {
    +bool Valid
    +double Min
    +double Max
    +double Mean
    +double Sigma
    +long Count
}

class MetricsHistorySeries {
    +double X
    +double Y
    +double YMin
    +double YMax
}

class BeatSegmentsSnapshot {
    +ulong Version
    +BeatSegment Segments
    +double LiftAngleDeg
    +BeatNoiseAverageSnapshot Average
}

class BeatSegment {
    +float Samples
    +double MsPerPoint
    +double StartTimeS
    +bool IsTic
    +double AOffsetMs
    +float PeakValue
    +double CPeakOffsetMs
    +double COnsetOffsetMs
}

class BeatNoiseAverageSnapshot {
    +bool SigmaEnabled
    +bool Frozen
    +int Lane1Count
    +int Lane2Count
    +float Lane1
    +float Lane2
    +double Lane1MeanPeak
    +double Lane2MeanPeak
}

class PixelBuffer {
    +int Width
    +int Height
    +uint Pixels
}

class TgConfig {
    +double SampleRate
    +TgBphMode BphMode
    +int ManualBph
    +double HpfCutoffHz
}

class TgResult {
    +TgSyncStatus SyncStatus
    +int DetectedBph
    +double MeasuredPeriodS
    +float ProcessedPcm
}

class TgEvent {
    <<abstract>>
    +double TimeSeconds
    +ulong SampleIndex
    +float PeakValue
    +TgEventType Type
}

class AEvent {
    +TgEventType A
}

class CEvent {
    +TgEventType C
    +double OnsetTimeSeconds
    +bool OnsetValid
}

AudioSource <|-- LiveAudioSource
AudioSource <|-- PlaybackSource
AudioSource <|-- SimSource
ScopeMarker <|-- ScopeVerticalMarker
ScopeMarker <|-- ScopeHorizontalMarker
ScopeMarker <|-- ScopeTextMarker
TgEvent <|-- AEvent
TgEvent <|-- CEvent

AnalysisRun "1" *-- "1" AnalysisRunSettings : configured by
AnalysisRun "1" *-- "1" AudioSource : selects
AnalysisRun "1" *-- "1" MasterAudioBuffer : owns
AnalysisRun "1" o-- "0..*" AnalysisFrame : produces

LiveAudioSource "1" --> "1" LiveAudioDevice : captures from
PlaybackSource "1" --> "1" WavFile : reads
SimSource "1" *-- "1" WatchSynthStreamConfig : uses

WavFile "1" *-- "1" WavFormatInfo : contains format
WavFile "1" --> "0..1" WavData : decoded as
WavData "1" --> "0..*" MasterAudioBuffer : supplies samples to
MasterAudioBuffer "1" --> "0..*" TgResult : analyzed into

TgConfig "1" --> "0..*" TgResult : configures detection
TgResult "1" *-- "0..*" TgEvent : contains

AnalysisFrame "1" *-- "0..*" GraphSeriesFrame : contains scope/rate series
AnalysisFrame "1" *-- "0..*" ScopeMarker : contains markers
AnalysisFrame "1" *-- "1" WatchMetricsUpdate : contains metrics
AnalysisFrame "1" o-- "0..1" PixelBuffer : contains sound image
AnalysisFrame "1" o-- "0..1" PixelBuffer : contains spectrogram image
AnalysisFrame "1" o-- "0..1" BeatMetricsHistorySnapshot : shares cumulative history
AnalysisFrame "1" o-- "0..1" BeatSegmentsSnapshot : shares recent beat windows

WatchMetricsUpdate "1" o-- "0..1" BeatTimingSample : per A event
WatchMetricsUpdate "1" o-- "0..1" AmplitudeSample : per C event
WatchMetricsUpdate "1" o-- "0..1" DerivedTimingMeasures : per A event
BeatMetricsHistorySnapshot "1" *-- "3" MetricsHistorySeries : rate/amplitude/beat error
BeatMetricsHistorySnapshot "1" *-- "2" StatsSummary : running stability stats
BeatMetricsHistorySnapshot "1" --> "1" WatchPosition : tags new beats as
BeatMetricsHistorySnapshot "1" *-- "0..6" PositionSummary : measured positions only
PositionSummary "1" --> "1" WatchPosition : aggregates
PositionSummary "1" *-- "3" StatsSummary : rate/amplitude/beat error
BeatSegmentsSnapshot "1" o-- "0..8" BeatSegment : recent beats, oldest first
BeatSegmentsSnapshot "1" *-- "1" BeatNoiseAverageSnapshot : scope 2 lane state
```

## Entity summary

| Entity | Source in project | Meaning |
|---|---|---|
| `WavFile`, `WavFormatInfo`, `WavData` | `Core.AudioIo` | Persisted or decoded audio data used for playback, recording, and verification |
| `AnalysisRunSettings` | `TimeGrapher.App` | User-selected run parameters converted into analysis worker configuration |
| `AudioSource` specializations | App run modes and Core workers | Live microphone, WAV playback, or synthetic signal input |
| `MasterAudioBuffer` | `Core.Shared` | Shared mono float ring buffer between input workers and analysis |
| `TgConfig`, `TgResult`, `TgEvent` | `Core.Detection` | Detector configuration, sync state, processed PCM, and tick/tock events |
| `AnalysisFrame` | `Core.Shared` | One UI update payload produced by an analysis pass |
| `GraphSeriesFrame`, `ScopeMarker`, `WatchMetricsUpdate`, `PixelBuffer` | `Core.Shared` | Data displayed as scope/rate graphs, markers, numeric results, and the sound-print / spectrogram images. The spectrogram payload (`AnalysisFrame.SpectrogramImage`) is the STFT of the recent 10 s input window built by `Core.Analysis.SpectrogramFrameProjector` — x = time, y = frequency (bins 0..~12 kHz, low at the bottom), color = dB magnitude through the 64-entry inferno-like LUT — published from a fixed three-buffer pool on the sound-print cadence |
| `BeatTimingSample`, `AmplitudeSample`, `DerivedTimingMeasures` | `Core.Shared` | Machine-readable per-beat values (rate error, signed beat error, locked BPH, amplitude, DiffTicTac/DiffPeriod/AvgPeriod) emitted per A/C event |
| `BeatMetricsHistorySnapshot`, `MetricsHistorySeries` | `Core.Shared` (built by `Core.Metrics.BeatMetricsHistory`) | Immutable cumulative history of rate/amplitude/beat-error series plus the latest readings and locked BPH, shared across frames; survives latest-wins frame coalescing |
| `StatsSummary` | `Core.Shared` (fed by `Core.Metrics.RunningStats`) | Running min/max/mean/population-σ since start for rate and amplitude — exact per-beat statistics independent of series decimation (Vario display) |
| `WatchPosition` | `Core.Shared` | Standard watch test positions per NIHS 95-10 / ISO 3158 (CH dial up, CB dial down, 6H crown left, 9H crown down, 3H crown up, 12H crown right); stamped on every snapshot as the position new beats are tagged with |
| `PositionSummary` | `Core.Shared` (aggregated by `Core.Metrics.BeatMetricsHistory`) | Per-position rate/amplitude/signed-beat-error running aggregates; only measured positions appear, bounded at the six standard slots (Test Positions display, future multi-position sequence) |
| `BeatSegmentsSnapshot`, `BeatSegment` | `Core.Shared` (built by `Core.Analysis.BeatSegmentCapture`) | Ring of the last 8 per-beat envelope windows (5 ms pre-roll, 400 ms, 1600 points) with A / C-peak / C-onset offsets, phase and lift angle; segment samples reference the capture's fixed 16-buffer pool and stay immutable until rotated out (Beat-Noise Scope; reused by beat-aligned waveform views) |
| `BeatNoiseAverageSnapshot` | `Core.Shared` (built by `Core.Analysis.BeatNoiseAverager`) | Scope 2 state: two phase-alternating 20 ms averaged lanes (800 points each) deliberately labeled trace 1/2 — never tic/toc — with per-lane interval counts, mean peak amplitude and the 50+50 cycle freeze flag |

## Relationship notes

| Relationship type | Representation in this project |
|---|---|
| 1:1 | One `AnalysisRun` has one `AnalysisRunSettings`, one selected `AudioSource`, and one `MasterAudioBuffer` |
| 1:n | One `AnalysisRun` produces many `AnalysisFrame` objects; one `TgResult` contains many `TgEvent` objects; one `AnalysisFrame` contains many graph series and markers |
| n:n | No native persisted many-to-many relationship exists because the app has no database and most runtime data is owned by a single run/frame |
| Generalization / specialization | `AudioSource` specializes into live/playback/sim sources; `TgEvent` specializes into A and C events; `ScopeMarker` specializes into vertical/horizontal/text markers |
| Aggregation / composition | `AnalysisFrame` is composed from graph series, markers, metrics, and the optional sound-print / spectrogram images (each a `PixelBuffer` from its projector's fixed publish pool); `WavFile` contains format metadata and can be decoded into `WavData`; `BeatMetricsHistorySnapshot` aggregates three `MetricsHistorySeries` plus up to six `PositionSummary` rows and is shared (aggregation, not owned) by many frames; `BeatSegmentsSnapshot` is shared the same way and aggregates (not owns) up to eight `BeatSegment` windows whose samples live in the capture's pooled buffers |
