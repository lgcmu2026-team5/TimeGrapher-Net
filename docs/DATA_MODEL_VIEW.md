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
| `GraphSeriesFrame`, `ScopeMarker`, `WatchMetricsUpdate`, `PixelBuffer` | `Core.Shared` | Data displayed as scope/rate graphs, markers, numeric results, and sound-print image |

## Relationship notes

| Relationship type | Representation in this project |
|---|---|
| 1:1 | One `AnalysisRun` has one `AnalysisRunSettings`, one selected `AudioSource`, and one `MasterAudioBuffer` |
| 1:n | One `AnalysisRun` produces many `AnalysisFrame` objects; one `TgResult` contains many `TgEvent` objects; one `AnalysisFrame` contains many graph series and markers |
| n:n | No native persisted many-to-many relationship exists because the app has no database and most runtime data is owned by a single run/frame |
| Generalization / specialization | `AudioSource` specializes into live/playback/sim sources; `TgEvent` specializes into A and C events; `ScopeMarker` specializes into vertical/horizontal/text markers |
| Aggregation / composition | `AnalysisFrame` is composed from graph series, markers, metrics, and optional sound image; `WavFile` contains format metadata and can be decoded into `WavData` |
