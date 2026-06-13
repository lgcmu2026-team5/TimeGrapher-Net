# TimeGrapher Qt/C++ → Avalonia/C# 포팅 계약

원본: `D:\TimeGrapher_Refactoring\` (Qt Widgets / CMake). 타겟: Windows와 Raspberry Pi에서 실행되는
Avalonia(net8.0) 앱.

> 현재 상태: 이 문서는 초기 포팅 계약 기록이다. 최신 사용자용 설명은
> `TimeGrapherNet\README.md`와 `TimeGrapherNet\docs\`를 기준으로 읽는다.
> 아래 상세 API 계약은 구현 이력 확인용이다.

## 포팅 원칙

1. **동작 동일성 우선.** 담당 C++ 파일을 정독하고 알고리즘·상수·분기·수식을 1:1로 옮긴다.
   임의 리팩터링/예외처리 추가 금지 (원본 AGENTS.md 정책 승계).
2. 이름은 C# 컨벤션(PascalCase)으로 바꾸되 원본 대응이 추적 가능해야 한다.
   (예: `tg_process` → `TgDetector.Process`, `mLocalGraphTicks` → `_localGraphTicks`)
3. 숫자 포맷은 culture 불변: `x.ToString("F2", CultureInfo.InvariantCulture)`.
   (`QString::arg(v, 0, 'f', 2)` 대응)
4. 진단 출력(`qInfo()`)은 `Console.Error.WriteLine(...)`.
5. 타입 매핑: `QVector<double>`→`List<double>`, `float*`+count→`ReadOnlySpan<float>`,
   `QString`→`string`, `QRgb`(0xAARRGGBB)→`uint`(동일 레이아웃, `Shared/Argb.cs`),
   `QImage(Format_ARGB32)`→`Shared/PixelBuffer.cs`, `QElapsedTimer`→`Stopwatch`,
   `quint64/uint64_t`→`ulong`.
6. WPF 사용 금지. Windows live audio와 Linux/Pi live audio는 플랫폼 경계 뒤에서 분리한다.

## 솔루션 구조 / 파일 소유권

```
TimeGrapherNet.sln
src/TimeGrapher.Core/                  net8.0 classlib (UI/플랫폼 의존 없음)
  Shared/Argb.cs                       [완성됨 — 수정 금지]
  Shared/PixelBuffer.cs                [완성됨 — 수정 금지]
  Shared/MasterAudioBuffer.cs          [완성됨 — 수정 금지]
  Shared/AnalysisFrame.cs              [완성됨 — 수정 금지] (frames, markers, WatchMetricsUpdate)
  Detection/*.cs                       [Agent A] Timegrapher.h/Detector.cpp/Dsp/Bph 포트
  Metrics/RollingAverage.cs            [Agent B]
  Metrics/RollingLeastSquares.cs       [Agent B]
  Metrics/WatchMetrics.cs              [Agent B]
  Imaging/SoundImageRenderer.cs        [Agent C]
  AudioIo/WavFileReader.cs             [Agent D]
  AudioIo/WavStreamWriter.cs           [Agent D]
  AudioIo/PlaybackWorker.cs            [Agent D]
  Sim/WatchSynthStream.cs              [Agent E]
  Sim/SimWorker.cs                     [Agent E]
  Analysis/AnalysisWorker.cs           [Agent F]
src/TimeGrapher.Platform.WindowsAudio/  Windows live audio backend (NAudio WaveInEvent)
  AudioCaptureWorker.cs
  SystemAudioControl.cs
src/TimeGrapher.App/                   Avalonia 11.3 + ScottPlot.Avalonia 5.0
  Program.cs, App.axaml(.cs)           [완성됨]
  Views/MainWindow.axaml(.cs)          [Agent G] 전체 UI
  Views/SplashWindow.axaml(.cs)        640x360 PNG sequence splash
  Assets/Splash/                       splash PNG frames + Source/splash5.mp4
  Rendering/GraphFrameRenderer.cs      [Agent G]
  Rendering/PixelBufferBitmap.cs       [Agent G] (PixelBuffer→WriteableBitmap 변환)
src/TimeGrapher.Verify/Program.cs      [Agent F] 헤드리스 검증 콘솔
```

다른 에이전트 소유 파일은 절대 수정하지 않는다. 자신의 파일만 생성/수정한다.

## 스레딩 계약

- 입력 워커(캡처/재생/심)는 **자기 스레드에서** `MasterAudioBuffer.WriteSamples()`로 쓰고,
  `DataReady` 이벤트를 발생시킨다 (Qt의 notify-only signal 대응 — payload 없음).
- UI 레이어가 `DataReady` → `AnalysisWorker.NotifyDataReady()`로 연결한다.
- `AnalysisWorker`는 자체 스레드 루프에서 `AutoResetEvent`를 기다리고, 깨어나면
  버퍼의 신규 구간을 처리한 뒤 **분석 스레드에서** `AnalysisFrameReady`를 발생시킨다.
- UI는 `Dispatcher.UIThread.Post()`로 프레임을 UI 스레드로 마샬링한다.
- 모든 워커의 `Stop()`은 스레드 종료를 join까지 보장한다 (Qt의 quit+wait 대응).
- 이벤트(`event Action` 등)는 어떤 스레드에서 발생하는지 XML doc 주석으로 명시한다.

## 모듈별 공개 API (고정)

### Agent A — Detection (`namespace TimeGrapher.Core.Detection`)

원본: `include/Timegrapher.h`, `src/Detector.cpp`(970줄), `src/Dsp.cpp|h`, `src/Bph.cpp|h`.
C 컨텍스트(`tg_context`)를 클래스 상태로 옮긴다. 내부 구현 파일은 자유롭게 분할
(예: `Dsp.cs`, `BphDetector.cs`) 하되 아래 공개 타입은 그대로:

```csharp
public enum TgBphMode { Auto = 0, Manual = 1 }
public enum TgSyncStatus { NotSynced = 0, Synced = 1, Mismatch = 2 }
public enum TgEventType { Unknown = 0, A = 1, C = 2 }
public enum TgCPlacement { Peak = 0, Onset = 1 }

public sealed class TgConfig
{
    public double SampleRate;            // 필수
    public TgBphMode BphMode;
    public int ManualBph;
    public double HpfCutoffHz;           // 0 = 기본값 200.0
    public double EnvelopeSmoothMs;      // 0 = 0.15
    public double SyncTolerancePct;      // 0 = 3.0
    public double AutoDetectSeconds;     // 0 = 1.5
    public int SyncLossMisses;           // 0 = 12
    public double PllPeriodGain;         // 0 = 0.01
    public double PllAcGain;             // 0 = 0.05
    public double OnsetFractionInit;     // 0 = 0.03
    public double MinPeakFractionInit;   // 0 = 0.20
    public bool SuppressPreSyncEvents;
    public TgCPlacement CPlacement;
    public static TgConfig Default();    // tg_config_default 대응
}

public struct TgEvent
{
    public double TimeSeconds;
    public ulong SampleIndex;
    public double SubSampleOffset;       // [-0.5, +0.5]
    public float PeakValue;
    public TgEventType Type;
    public bool IsPreSync;
    public ulong OnsetSampleIndex;
    public double OnsetSubSampleOffset;
    public double OnsetTimeSeconds;
    public bool OnsetValid;
}

public sealed class TgResult            // tg_result_t. Process/Flush가 내용을 채움(재사용 가능)
{
    public TgSyncStatus SyncStatus;
    public int DetectedBph;
    public double MeasuredPeriodS;
    public List<TgEvent> Events = new();         // 호출마다 Clear 후 채움
    public float[] ProcessedPcm = Array.Empty<float>(); // 길이는 ProcessedPcmLen
    public int ProcessedPcmLen;
    public ulong ProcessedPcmStartSample;
    public bool SyncLostEvent;
    public bool SyncAcquiredEvent;
    public bool DetectorResetEvent;
    public float OnsetThreshold;
    public float MinPeakThreshold;
    public float NoiseFloor;
    public float ReferencePeak;
}

public sealed class TgDetector          // tg_init/tg_destroy → ctor (관리 메모리라 Dispose 불필요)
{
    public TgDetector(TgConfig cfg);
    public void Process(ReadOnlySpan<float> pcm, TgResult result); // tg_process (실패시 예외 아님—원본은 int 리턴이지만 실패 경로가 인자검증뿐이면 void 유지)
    public void Flush(TgResult result);  // tg_flush
    public void Reset();                 // tg_reset
    public double OnsetFraction { get; set; }      // tg_get/set_onset_fraction ([0.001,0.9] 클램프)
    public double MinPeakFraction { get; set; }
    public TgCPlacement CPlacement { get; set; }
}
```

### Agent B — Metrics (`namespace TimeGrapher.Core.Metrics`)

원본: `include/WatchMetrics.h`, `src/WatchMetrics.cpp`, `RollingAverage.*`, `RollingLeastSquares.*`.
`WatchMetricsUpdate`는 `Shared/AnalysisFrame.cs`에 이미 정의됨 — 그것을 사용.

```csharp
public sealed class RollingAverage      // 원본 헤더의 공개 메서드를 그대로 매핑
public sealed class RollingLeastSquares // 〃

public struct WatchMetricsConfig
{
    public int SampleRate;          // 48000
    public double LiftAngle;        // 52.0
    public int AveragingPeriod;     // 2
    public int MaxRateDataPoints;   // 250
    public double RateErrorYScale;  // 10.0
    public int RlsWindowInit;       // 100
}

public sealed class WatchMetrics
{
    public WatchMetrics(WatchMetricsConfig config);
    public void Reset();
    public WatchMetricsUpdate HandleAEvent(double eventSample, bool haveValidBph, double bph);
    public WatchMetricsUpdate HandleCEvent(double eventSample, bool haveValidBph, double bph);
    public int CurrentBeatPhase();
    public static double Amplitude(double liftAngle, double t1, double bph);
}
```

### Agent C — SoundImage (`namespace TimeGrapher.Core.Imaging`)

원본: `include/SoundImageRenderer.h`(566줄, 문서 충실), `src/SoundImageRenderer.cpp`(1042줄).
QImage 대신 `Shared/PixelBuffer.cs`에 그린다 (`SetPixel`/`Fill`/`Pixels` 직접 접근 가능).

```csharp
public sealed class SoundImageRenderer
{
    public enum VerticalTimeDirection { BottomUp = 0, TopDown = 1 }

    public sealed class Config
    {
        public double SampleRateHz = 48000.0;
        public double Bph = 0.0;                    // <=0 = BPH 미정 모드
        public uint SoundColor = Argb.Rgba(255, 0, 0);
        public uint BackgroundColor = Argb.Rgba(255, 255, 255);
        public VerticalTimeDirection Direction = VerticalTimeDirection.BottomUp;
        public int WarmupColumns;    // 원본 Config와 동일 필드 전부 포함할 것
        public int AnchorColumns;
        public float Gamma;
        public bool LivePreviewCurrentColumn;
        // …원본 Config의 나머지 필드도 동일 기본값으로 포함…
    }

    public bool Initialize(PixelBuffer image, Config cfg);
    public void Reset();                                  // reset()
    public void Reset(ulong nextInputAbsoluteSampleIndex);
    public void SetSoundColor(uint color);
    public void SetBackgroundColor(uint color);
    public void SetBph(double bph);
    public void SetSampleRate(double sampleRateHz);
    public void SetVerticalTimeDirection(VerticalTimeDirection direction);
    public void ProcessSamples(ReadOnlySpan<float> samples);
    public void MarkAEventAbsoluteSampleIndex(ulong absoluteSampleIndex, uint color, int markerSidePixels);
    public void MarkCEventAbsoluteSampleIndex(ulong absoluteSampleIndex, uint color, int markerSidePixels);
    public int ImageWidth { get; }
    public int ImageHeight { get; }
    public bool BphValid { get; }
    public double CurrentBph { get; }
    public double SamplesPerColumnExact { get; }
}
```

(호출부는 `markAEventAbsoluteSampleIndex(event_sample, …)`에 double을 넘기지만 원본
시그니처는 quint64라 암묵 절단이 일어난다 — 동일하게 `(ulong)` 캐스트로 절단해 호출한다.
이 캐스트는 호출자(Agent F)가 수행.)

### Agent D — Audio I/O (`namespace TimeGrapher.Core.AudioIo`, Windows capture는 `TimeGrapher.Platform.WindowsAudio`)

원본: `src/WindowsAudio.cpp`(QAudioSource 경로 참고용), `src/PlaybackWorker.cpp`,
`src/WavStreamWriter.cpp`, `include/WaveHeader.h`. WAV/Playback은 Core에 두고,
Windows 라이브 캡처는 플랫폼 프로젝트에서 **NAudio `WaveInEvent`** 를 사용한다.
Linux/Pi live capture는 App 경계에서 PipeWire/ALSA command로 처리한다.

```csharp
public sealed class WavData
{
    public int SampleRate;
    public float[] Samples;   // mono, [-1,1] (스테레오면 채널0)
}

public static class WavFileReader
{
    /// PCM16/24/32, IEEE float WAV 지원. 실패 시 예외.
    public static WavData ReadMonoFloat(string filePath);
}

public sealed class WavStreamWriter : IDisposable   // 원본 WavStreamWriter.h와 동일 동작
{
    public bool Open(string filePath, int sampleRate, int channels);
    public bool Write(ReadOnlySpan<float> samples);
    public bool Close();           // RIFF/data 크기 패치
    public bool IsOpen { get; }
    public ulong FramesWritten { get; }
    public int SampleRate { get; }
    public int Channels { get; }
}

// Windows capture는 src/TimeGrapher.Platform.WindowsAudio/AudioCaptureWorker.cs에 둔다.

public sealed class PlaybackWorker : IDisposable       // TPlaybackWorker 대응
{
    public PlaybackWorker(MasterAudioBuffer buffer, int samplesPerSecond);
    public event Action? DataReady;          // 재생 스레드에서 발생
    public event Action? DoneReadingFile;    // 파일 끝(취소 아님)에서 1회
    public void Start(string fileName);      // 전용 스레드 시작, 원본과 같은 실시간 페이싱
    public void Stop();                      // 취소 + join
}
```

원본 워커의 2초 주기 FPS/SPF/SPS 통계 갱신(`buffer.SetStats`) 로직도 그대로 옮긴다.

### Agent E — Sim (`namespace TimeGrapher.Core.Sim`)

원본: `include/WatchSynthStream.h`(326줄), `src/WatchSynthStream.cpp`(567줄), `src/SimWorker.cpp`.
C 스타일 config/함수를 클래스로. **`WatchSynthStreamConfig`는 원본 struct의 모든 필드**를
같은 이름(PascalCase)·같은 기본값으로 포함. 원본의 config 헬퍼들
(`watch_synth_stream_default_config`, `watch_synth_stream_realistic_config` 등)은
`WatchSynthStreamConfig.Default()` / `.Realistic()` 정적 팩토리로.

```csharp
public sealed class WatchSynthStreamConfig { /* 원본 전 필드 */ public static WatchSynthStreamConfig Default(); public static WatchSynthStreamConfig Realistic(); }

public sealed class WatchSynthStream     // 원본 스트리밍 API 대응 (reset/generate 블록 채움)
{
    public WatchSynthStream(WatchSynthStreamConfig cfg, int sampleRate);
    public void Generate(Span<float> block);   // 다음 연속 블록 채움 (원본 fill 함수 대응)
}

public sealed class SimWorker : IDisposable    // TSimWorker 대응
{
    public SimWorker(MasterAudioBuffer buffer, int samplesPerSecond);
    public event Action? DataReady;   // 심 스레드에서 발생
    public event Action? SimDone;     // (원본 SimDone 대응 — 원본이 유한 길이면 동일 조건)
    public void Start(WatchSynthStreamConfig cfg);  // 전용 스레드, 실시간 페이싱
    public void Stop();
}
```

(원본 WatchSynthStream의 공개 함수 이름/시그니처가 위와 다르면 **원본 구조를 우선**하되,
SimWorker가 노출하는 이벤트/메서드 계약은 유지한다.)

### Agent F — Analysis (`namespace TimeGrapher.Core.Analysis`) + Verify

원본: `src/AnalysisWorker.cpp`(362줄, 위임 구조 명확). A/B/C/D 모듈의 위 계약 API만 사용.

```csharp
public sealed class AnalysisWorker : IDisposable
{
    public sealed class Config
    {
        public int SampleRate = 48000;
        public double LiftAngle = 52.0;
        public int AveragingPeriod = 2;
        public bool UseCOnset = false;
        public ulong SessionId = 0;
        public bool AutoBph = true;
        public int ManualBph = 0;
        public double HpfCutoffHz = 0.0;
        public int SoundImageWidth = 0;
        public int SoundImageHeight = 0;
        public WavStreamWriter? WavWriter = null;
    }

    public AnalysisWorker(MasterAudioBuffer buffer, Config config);
    public event Action<AnalysisFrame>? AnalysisFrameReady;  // 분석 스레드에서 발생
    public void Start();             // 분석 스레드 시작 (AutoResetEvent 대기 루프)
    public void NotifyDataReady();   // 아무 스레드에서나 호출 가능 (HandleInputData 트리거)
    public void Stop();              // 루프 종료 + join
    public void Dispose();
}
```

`HandleInputData()` 1회 수행 = 원본과 동일: 4096 샘플 슬라이스 반복, WavWriter,
SoundImageRenderer.ProcessSamples, TgDetector.Process, 마커/메트릭/시리즈 빌드,
2초 주기 foreground 통계, 프레임 1개 emit. `frame.SoundImage`는 **Clone() 스냅샷**.

**Verify 콘솔** (`src/TimeGrapher.Verify/Program.cs`): 인자로 WAV 경로(들)를 받아
스레드 없이 직접 `WavFileReader` → 4096 슬라이스 → `TgDetector.Process` →
`WatchMetrics.Handle*` 호출로 파이프라인을 돌리고, 마지막에
`file, detected_bph, sync_status, final results text`를 한 줄씩 출력. 파일명에서
기대 BPH(예: `21600BPH_*.wav` → 21600)를 파싱해 검출 BPH와 비교, 전부 일치하면
exit 0, 아니면 exit 1.

### Agent G — UI (`namespace TimeGrapher.App.*`)

원본: `forms/MainWindow.ui`(916줄), `src/MainWindow.cpp`(895줄), `src/Main.cpp`,
`src/GraphFrameRenderer.cpp`(371줄), `src/SoundImageWidget.cpp`.

- `Views/MainWindow.axaml`: 원본 .ui 레이아웃 재현 — 좌측 제어 패널(Run/Misc/Sim/Watch
  프레임: 장치 콤보, Refresh/Start/Stop, 샘플레이트/평균주기/모드 콤보, 게인 슬라이더,
  HPF 입력, C-onset 체크, 스코프 스케일, Results 라벨, Sim BPH/진폭/비트에러/에러레이트/
  Realistic, Watch BPH 콤보/리프트각), 우측 Scope plot + Rate/Sound 탭(`TabControl`),
  하단 상태바(TextBlock).
- `Views/SplashWindow.axaml`: 640x360 borderless 창에서 `Assets/Splash/splash_0001.png`
  ... `splash_0122.png`를 24fps로 재생한 뒤 `MainWindow`로 전환한다.
- `Views/MainWindow.axaml.cs`: 원본 MainWindow.cpp의 슬롯/시작·중지/세션 id/모드 전환
  로직 포트. 워커 수명주기 관리, `Dispatcher.UIThread.Post`로 프레임 수신.
- `Rendering/GraphFrameRenderer.cs`: QCustomPlot → ScottPlot 5 (`AvaPlot.Plot`).
  - Scope: frame-local append가 아니라 bounded snapshot을 받아 기존 ScottPlot series 데이터를 교체한다.
    x축은 `frame.GraphTickEnd` 우측 정렬, 폭 `sampleRate / scopeScale`.
  - Rate: replace 시리즈 (`rate.tic` 파랑/`rate.toc` 빨강 — 원본 createGraphs 색 그대로).
  - 마커: 수직선/수평(Outward/Inward)/텍스트 — ScottPlot Line/Text plottable로 재현,
    purge 범위의 마커 제거 포함.
  - Results 라벨/사운드 이미지/상태바 갱신, 세션 id 필터링은 MainWindow 쪽 로직 그대로.
- `Rendering/PixelBufferBitmap.cs`: `PixelBuffer`(ARGB32) → `WriteableBitmap`
  (`PixelFormat.Bgra8888`; ARGB(uint, little-endian 메모리 = BGRA 바이트) 그대로 복사 가능)
  변환 + `Image` 컨트롤 갱신 헬퍼.
- 라이브 모드 장치/샘플레이트 enumerate는 App의 `LiveAudioBackend`를 통해 플랫폼별 worker로 위임한다.
- 녹음(WavStreamWriter) 경로: 원본 RecordSessionCheck 동작 확인 후 동일하게 (없으면 생략).

## 빌드/검증

- 빌드: `dotnet build TimeGrapherNet.sln -c Release` (사내망 첫 복원 ~3분).
- 헤드리스 검증: `dotnet run --project src/TimeGrapher.Verify -- D:\TimeGrapher_Refactoring\samples\*.wav`
- GUI: `dotnet run --project src/TimeGrapher.App`
- Windows publish smoke: `TimeGrapher.App.exe --smoke`
- Raspberry Pi publish smoke: `./TimeGrapher.App --smoke`
- Raspberry Pi GUI smoke: `DISPLAY=:0 ./TimeGrapher.App` 실행 후 스플래시 길이보다 오래 유지되는지 확인
