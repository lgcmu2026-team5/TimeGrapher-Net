# SAP 기준 Architecture Tactics & Design Patterns 분석

> CMU-LG Software Architecture Training Course 과제 문서.
> 기준 교재: Bass·Clements·Kazman, *Software Architecture in Practice* (이하 **SAP**).
>
> 이 문서는 TimeGrapherNet에 실제로 적용된 tactic·pattern을 **코드 근거로 검증**해 정리한다.
> 표의 마지막 열은 교과서 정의에 대한 적용도다 — **✓ 완전 적용**, **△ 유사하나 부분 적용**, **✗ 기각**.

## 개요 — 아키텍처를 지배하는 한 가지 문제

TimeGrapher는 시계 소리를 받아 실시간으로 분석·표시하는 앱이다(입력 → 검출 → 측정 → 화면).
실시간 앱이므로 설계는 세 가지 압력에서 출발한다.

1. **성능** — UI 주 스레드가 막히면 화면이 멈춘다.
2. **변경용이성** — 분석 로직이 UI·OS와 섞이면 바꾸기 어렵다.
3. **이식성** — Windows와 라즈베리파이 5를 한 코드로 돌려야 한다.

아래의 거의 모든 tactic은 이 세 압력에서 파생된다.

---

## 실시간 마감 예산 — 28800 BPH = 비트당 125 ms

성능 tactic들이 "무엇에 대한" tactic인지 정량적으로 못박는다. 기계식 시계는
BPH(시간당 진동수)마다 비트를 내며, 비트 주기 = 3600 s / BPH. 기준인 28800 BPH는
초당 8비트, 즉 **125 ms마다 한 비트**다. 오디오는 끊임없이 들어오므로 비트당
처리(캡처 → DSP/검출 → 메트릭 → 투영)가 비트 주기보다 오래 걸리면 백로그가 쌓인다.

| BPH | 18000 | 21600 | 28800 (기준) | 36000 | 43200 |
|---|---|---|---|---|---|
| 비트 주기 | 200.0 ms | 166.7 ms | **125.0 ms** | 100.0 ms | 83.3 ms |

### 처리 단위와 여유분

- 분석 패스는 **4096샘플 청크**(48 kHz에서 85.3 ms 분량) 단위로 돌고
  (`AnalysisWorker.cs`), 패스 시작 시점의 스냅샷까지만 드레인하므로 한 패스가
  라이브 쓰기를 무한정 쫓지 않는다.
- 일시적 초과는 큐 증가가 아니라 **다음 패스의 더 큰 배치**로 흡수된다. 30초
  링버퍼는 28800 BPH 기준 **240비트 분량의 슬랙**이다. 그 이상 밀리면 가장 오래된
  샘플부터 드롭하고 `InputOverrun`/`InputSamplesDropped`로 계측한다
  (`MasterAudioBuffer.cs`).
- 증상 신호는 `AnalysisLagSamples`(패스 종료 시점 백로그)와
  `ProcessingElapsedMs`(패스 소요 시간)다.

### 단계별 비용 특성 (48 kHz, Pi 5 기준 정성 추정)

| 단계 | 비트당 비용 | 비고 |
|---|---|---|
| 캡처 콜백 + 링 쓰기 | ~µs | stackalloc 변환 + 2세그먼트 블록카피, 정상 상태 무할당 |
| DSP 체인(HPF→Envelope→Detector) | 코어의 ~0.05–0.2% | O(n) 스트리밍, 상수 상태 |
| 검출/메트릭 | ~µs | O(1) 증분(`RollingLeastSquares`, PLL), 스크래치 재사용으로 무할당 |
| 사운드프린트 컬럼 렌더 + 마커 | 수십 µs | 마커→컬럼 조회 O(1) (선형 탐색에서 개선) |
| 사운드프린트 발행 | ~0.5–1 ms, ≤10회/s | 고정 3버퍼 풀 복사(기본 크기 ~2.67 MB), LOH churn 0 |
| 스펙트로그램 STFT 컬럼 렌더 | 수십 µs | 1024-pt FFT/hop(48 kHz 기준 비트당 ~12 hop), 스크래치 재사용 무할당 |
| 스펙트로그램 발행 | ~0.25 ms, ≤10회/s | 사운드프린트와 동일한 고정 3버퍼 풀 복사(~0.96 MB) |
| UI (활성 탭 1개) | 33/100 ms 스로틀 | 8000/250 포인트 예산, latest-wins 합류, 마커 plottable 풀링. pause 종료 커서 제거만 `RenderToAll` 1회 예외 |

### 마감 강제 — AnalysisDeadlineMonitor

측정만 하던 텔레메트리에 반응을 붙였다(tactic: **bound execution times / manage
work requests** — 점진적 저하). 패스마다 백로그를 **비트 주기 단위**(공칭 락
주기 `MeasuredPeriodS`(3600/bph), 락 전에는 125 ms 기본)로 환산해 2비트 예산과
비교하고, 연속 16패스 초과 시 시각 비용이 싼 순서로 저하한다:

1. 진행 중 사운드프린트 컬럼과 스펙트로그램 라이브 에지 커서의 실시간 갱신 중단
2. 사운드프린트·스펙트로그램 발행 간격 100 ms → 400 ms, 스윕·멀티필터 시리즈
   발행 플로어 50 ms → 400 ms (지속 2비트 위반 중에는 패스당 스트림 전진이
   250 ms 이상이므로, 플로어가 그보다 길어야 위반 도중에도 게이트가 닫힌다)
3. 스코프 데시메이션 stride 2배 + 신규 비트 세그먼트 윈도 개방 중단
   (Beat-Noise 탭이 전진을 멈춤; 열린 윈도는 자연 완료)

연속 48패스 회복(0.5비트 미만) 시 한 단계씩 복귀한다(히스테리시스로 진동 방지).
현재 레벨은 프레임에 실려 상태바에 "rendering quality reduced"로 표시된다
(`AnalysisDeadlineMonitor.cs`, `AnalysisRunStatusReporter.cs`). 백로그(lag)를 위반
신호로 쓰는 이유: 단일 패스 소요는 4096샘플로 바운드되어 정규화 없이는 예산과
비교할 수 없고, 백로그가 곧 "비트당 작업 > 비트 주기"의 적분적 증상이기 때문이다.

남은 검증: Pi 5 라이브 마이크 실측으로 단계별 비용 추정을 수치로 대체하는 것
(ARCHITECTURE_REVIEW_FIXES.md의 미완 항목과 동일).

---

## 1. Architecture Tactics (품질속성별)

### 변경용이성 (Modifiability)

| Tactic | 적용 방식 | 근거 | |
|---|---|---|---|
| **restrict dependencies** | Core는 외부 참조 0개. App→Platform→Core 단방향 비순환. **CI가 grep으로 Core 안의 NAudio/Platform 참조를 차단**하고, OS별 publish에 잘못된 DLL이 섞이면 빌드 실패 | `Core.csproj`, `.github/workflows/ci.yml` | ✓ |
| **encapsulate** | OS 오디오 스택(NAudio / pw-record)을 Core 소유 인터페이스 `ILiveAudioWorker : IAudioInputWorker` 뒤에 은닉 | `ILiveAudioWorker.cs`, `IAudioInputWorker.cs` | ✓ |
| **use an intermediary** | `LiveAudioBackend` 한 파일만 구체 OS 타입을 알고 분기. 나머지 App은 인터페이스만 사용 | `LiveAudioBackend.cs` | ✓ |
| **increase semantic coherence** | Core = 분석(Detection/Metrics/Imaging/Sim)만 담당. UI·OS 책임 없음 | `Core.csproj` | ✓ |
| **split module** | 비대해지던 `MainWindow`를 partial 5개 + 추출 서비스(`RunCommandService`, `RunSessionController`, `MainWindowSelectionCoordinator`)로 분해 | `MainWindow.*.cs` | ✓ |
| **defer binding + use an intermediary (ML 소켓)** | 베토 전용 `IBeatEventGate`를 Core에 선언하고 `BeatEventGateHost`가 검출→메트릭 초크포인트에서 중개. 고전 구현(`PllMatchGate`)은 지금 출하, 미래 ONNX TinyML 게이트는 리프 추론 프로젝트에서 같은 인터페이스를 구현해 합성 루트(App 토글/Verify `--gate`)에서 주입 — Core는 무의존 유지. 게이트는 이벤트를 생성·재타이밍할 수 없고 BPH/PLL은 항상 원시 스트림을 보므로, 오동작 ML이 락을 깨뜨릴 수 없는 **구조적** 안전 | `IBeatEventGate.cs`, `BeatEventGateHost.cs`, `AnalysisRunSettings.cs` | ✓ |
| **anticipate expected changes (옵션 seam)** | 강건성 옵션을 동결 계약(`TgTypes.cs`)에 손대지 않고 `TgDetector(TgConfig, TgDetectorOptions?)` 오버로드로 도입. all-off/null이면 비트동일 — 골든마스터·패리티 테스트·Verify `--fidelity-check` 3중 장치가 핀 | `TgDetectorOptions.cs`, `DetectorGoldenMasterTests.cs`, `FidelityCheck.cs` | ✓ |

### 성능 (Performance) — 실시간 UI의 핵심

| Tactic | 적용 방식 | 근거 | |
|---|---|---|---|
| **introduce concurrency** | 입력·분석·녹음이 각자 전용 스레드(분석은 `Priority.Highest`), UI 스레드는 렌더링만. 생산자는 소비자를 기다리지 않음 | `AnalysisWorker.cs` | ✓ |
| **limit event response** | 렌더 스케줄러가 **"최신 프레임 1개"만 유지** — 렌더 진행 중 들어온 프레임은 병합/폐기(`_droppedFrames`)하고, 일회성 신호(오버런 등)는 병합으로 보존 | `AnalysisFrameRenderScheduler.cs` | ✓ |
| **schedule resources** | 일반 프레임은 모든 탭이 가벼운 `ObserveFrame`만 받고 **활성 탭만** 무거운 `RenderFrame` 수행. pause 종료 때 리뷰 커서를 지우기 위한 `RenderToAll`은 저장된 마지막 프레임을 한 번 다시 그리는 예외라 입력/분석 작업을 늘리지 않음 | `AnalysisFrameRouter.cs`, `MainWindow.axaml.cs` | ✓ |
| **bound queue sizes** | 녹음 큐 = `BlockingCollection(128)`. 초과 시 블록을 **드롭**(분석 스레드를 막지 않음) | `QueuedWavStreamWriter.cs` | ✓ |
| **reduce overhead** | 롤링 집계 O(1)(`RollingAverage/LeastSquares`), 그래프 점 수를 예산(8000/250)으로 다운샘플(`SeriesDataReducer`), `ArrayPool`·비트맵 재사용 | 다수 | ✓ |
| **manage sampling rate** | 입력 워커를 Stopwatch 기준 10ms 주기로 페이싱; 노이즈 플로어를 매 샘플이 아닌 ~1ms마다 데시메이션 | `SimWorker.cs`, `Detector.cs` | ✓ |
| **maintain multiple copies of data** | 30초 링버퍼로 읽기/쓰기 속도 분리 + 사운드프린트 발행은 **고정 3버퍼 풀을 로테이션**하는 스냅샷 복사(발행된 버퍼는 2회의 더 새로운 발행 이후에만 재사용 → UI가 안전하게 읽는 동안 분석 스레드는 계속 갱신, 정상 상태 할당 0). 스펙트로그램 STFT 이미지 발행도 **동일한 고정 3버퍼 풀 로테이션**을 재사용한다. 비트 노이즈 세그먼트 발행도 같은 패턴: **고정 28버퍼 풀(float[1600])에서 발행 기준으로 재사용을 게이트** — 완료 링과 최근 2개 스냅샷이 참조하는 버퍼는 재사용 스캔에서 제외되어, UI가 읽는 동안 불변 계약 유지. UI는 최신 스냅샷의 세그먼트만 읽고 캐시하지 않는다 | `MasterAudioBuffer`, `SoundPrintFrameProjector.cs`, `SpectrogramFrameProjector.cs`, `BeatSegmentCapture.cs` | ✓ |
| **bound execution times / manage work requests** | `AnalysisDeadlineMonitor`가 패스 백로그를 **비트 주기 단위**로 환산해 2비트 예산 초과가 지속되면 점진 저하 사다리(라이브 프리뷰 중단 → 발행 간격 확대 → stride 증가) 실행, 지속 회복 시 단계 복귀. 스펙트로그램 프로젝터도 같은 노브(라이브 에지 커서 중단, 발행 간격 4배)를 노출해 사다리 레벨 1·2에 함께 배선되어 있다. 위 "실시간 마감 예산" 절 참조 | `AnalysisDeadlineMonitor.cs`, `AnalysisWorker.cs` | ✓ |
| **bound resource usage (장기 히스토리)** | 비트 단위 메트릭 히스토리(`BeatMetricsHistory`)를 **고정 용량 `DecimatingSeries`**에 누적 — 가득 차면 인접 포인트 쌍을 병합해 해상도를 반감(버킷 min/max 보존). 실행이 몇 시간이어도 메모리·발행 비용이 일정("1시간째 비용 = 1초째 비용"). 스냅샷은 스트림 시간 0.5초당 1회만 재구성, 그 사이 프레임은 같은 불변 인스턴스 공유. 다중 포지션 시퀀스도 `WatchPositions.Count`(10) 슬롯 배열과 스냅샷 버전 게이트로 제한된다. 누적은 Core에서 수행 — 렌더 스케줄러의 latest-wins 병합이 프레임을 폐기해도 데이터 손실 없음 | `DecimatingSeries.cs`, `BeatMetricsHistory.cs`, `MultiPositionSeqRenderer.cs` | ✓ |
| **record/monitor (레이턴시 증거)** | QA가 요구하는 캡처→처리→표시 레이턴시를 단일 Stopwatch 시계로 계측: `MasterAudioBuffer`가 쓰기마다 (sampleEnd, ticks) 256개 스탬프 링을 유지, `AnalysisWorker`가 프레임에 `CaptureTimestamp`/`ProcessingCompletedTimestamp`를 스탬핑, UI가 렌더 직후 표시 시각을 더해 구간별 평균/최악값을 집계(`LatencyStatsTracker`, 상태바 우측 표시). 누락 비트(`WatchMetrics.MissedBeats`)·싱크 손실은 **세션 누적 카운터**로 프레임에 실려 latest-wins 병합에도 보존 | `MasterAudioBuffer.cs`, `LatencyStatsTracker.cs` | ✓ |

### 가용성 (Availability) — 시작/중지 안정화와 결함 입력 처리

| Tactic | 적용 방식 | 근거 | |
|---|---|---|---|
| **timestamp (논리 시퀀스)** | **핵심.** 실행마다 단조증가 `_runSessionToken`을 발급, 모든 비동기 콜백이 토큰을 들고 옴 → 이전 실행의 늦은 응답을 토큰 불일치로 폐기(`AnalysisSessionId`, 렌더 `_generation`까지 3중) | `RunSessionController.cs` | ✓ |
| **exception handling / detection** | 워커 스레드는 예외를 try/catch로 가둬 프로세스를 죽이지 않고 `Failed`로 보고; `_stopRequested`로 "정상 중지"와 "장치 사망"을 구분해 `CaptureEnded` 발생 | `PlaybackWorker.cs`, `AudioCaptureWorker.cs` | ✓ |
| **degradation** | Linux 입력 장치 열거는 PipeWire `wpctl` 결과가 없으면 ALSA `arecord -l`로 폴백한다. 사용자가 선택한 ALSA 장치는 `arecord`/S16_LE 경로로 캡처되어 PipeWire 장치가 없어도 낮은 수준의 캡처 경로를 제공한다 | `LinuxLiveAudioWorker.cs` | ✓ |
| **ignore faulty input + state resynchronization (검출 갭)** | 반 비트를 초과하는 A-A 간격을 단일 기준으로 "검출 갭"으로 분류해 3중 대응: ① 갭에 걸친 부호 비트오차/주기 델타를 무효화(ignore faulty input) ② 틱/톡 비트 카운터를 물리 위상에 재앵커 ③ 레이트 회귀(RLS) 윈도를 갭에서 재시작(state resynchronization, 새 싱크 락과 동일한 회복 — 위상당 2포인트면 판독 복귀). 비트 1개 누락이 부호를 반전시키거나 누적 통계 min/max를 영구 오염시키지 않으며, 누락 비트는 보간 없이 제외되고 `MissedBeats` 세션 카운터로 기록된다 | `WatchMetrics.cs` | ✓ |
| **condition monitoring + degradation recovery (약신호, 옵트인)** | `EnableAdaptiveFloor`: 기각 버스트가 섀도 중앙값 통계를 남겨(condition monitoring) 10×노이즈 하드 플로어(minPeakThr ≈ 2.8×n) 아래의 약한 시계로 기준이 하향 적응하고, 수락 공백 후 기준 피크가 지수 감쇠 + 히스토리 재시작으로 큰소리→조용함 래치를 자가 복구(degradation recovery). 측정: quiet-step 행에서 베이스라인은 게인 스텝 후 영구 무락, 활성 시 재락 + 사후 recall 0.694 | `Detector.cs`, `AdaptiveFloorTests.cs` | ✓ |
| **fault detection with hysteresis (임펄스 잡음, 옵트인)** | `EnableRegimeGuard`: V5.6 순간 레짐 트립을 run-of-3 지속 카운터로 디바운스 — 단발 임펄스(문 쾅)는 다음 정상 틱이 런을 리셋해 전체 플러시(BPH/PLL/히스토리 소거)를 일으킬 수 없고, 진짜 이득 변화는 ~3비트 내 정상 트립. 측정: impulse-dos 행 리셋 7→0, NotSynced→Synced | `Detector.cs`, `RegimeGuardTests.cs` | ✓ |
| **sanity checking / voting (메트릭 보호, 옵트인)** | `PllMatchGate`: PLL 위상 매치에 실패한 이벤트를 메트릭 도달 전에 베토(베토된 A는 페어 C도 차단 — 고아 C의 가짜 진폭 방지). 측정: impulse-storm 정밀도 0.911→0.981, 락 획득 시점 불변. 정직한 한계: 극한 잡음(noisy-1)에서는 PLL 위상 자체가 오염되어 역효과(recall 0.167→0.042) — 위상 비의존 형태 분류기(TinyML)의 문서화된 헤드룸 | `PllMatchGate.cs`, `PllMatchGateTests.cs` | ✓ |

### 시험용이성 (Testability)

| Tactic | 적용 방식 | 근거 | |
|---|---|---|---|
| **sandbox + limit nondeterminism** | `WatchSynthStream`이 SplitMix64 PRNG를 **시드 고정**해 결정론적 시계 신호 생성. `Clean()`은 모든 확률 요소를 끔 | `WatchSynthStream.cs` | ✓ |
| **abstract data sources** | mic·WAV·합성이 모두 `IAudioInputWorker`/`engine.Process(span)` 뒤에서 동일하게 소비되어, 파일로 결정론적 검증 가능 | `DetectorMetricsEngine.cs` | ✓ |
| **specialized interfaces** | GUI 없는 `Verify` 콘솔, `--smoke`/`--audio-smoke`/`--capture-smoke` 진입점(종료코드 0/2/3), `InternalsVisibleTo` 테스트 훅 | `Verify/Program.cs`, `Program.cs`, `AudioSmokeRunner.cs` | ✓ |
| **executable assertions** | Verify가 파일명의 기대 BPH와 검출 BPH를 대조해 exit code 반환 → **CI가 매 푸시 실행**. 확장: `FillF32` 그라운드트루스 사이드채널 채점(`DetectionScorer` — 이벤트 수준 정밀도/재현율/타이밍), `--adverse` 악조건 행 9종(약신호·잡음·임펄스 폭풍·게인 스텝·무음 선행·잡음 단독), `--ab=baseline,robust` **양방향 게이트**(베이스라인 팔은 기지 약점을 핀, robust 팔은 개선을 증명 — "개선이 조용히 죽음"과 "클린 경로 파손" 모두 CI 실패) | `Verify/Program.cs`, `AdverseScenarios.cs`, `DetectionScorer.cs` | ✓ |
| **record/playback (충실도 3중 장치)** | ① 골든마스터: 강건성 작업 착지 **전**에 기록한 절대 이벤트 시퀀스 핀(동일 바이너리 A/B가 못 보는 상시 드리프트 검출) ② null vs all-off 옵션 패리티 단위 테스트 ③ Verify `--fidelity-check` 인프로세스 블록 단위 동일성 CI 게이트(플랫폼 libm 편차 면역) | `DetectorGoldenMasterTests.cs`, `DetectorOptionsFidelityTests.cs`, `FidelityCheck.cs` | ✓ |
| **controlled fault injection** | 합성기에 포아송 임펄스 잡음 노브(전용 RNG 스트림 — 켜도 틱/지터 시퀀스 비트동일, rate 0이면 출력 비트동일). 균일 백색잡음으로는 재현 불가능하던 레짐 리셋 폭풍·중앙값 오염·PLL 래치를 결정론적으로 재현 | `WatchSynthStream.cs`, `WatchSynthImpulseNoiseTests.cs` | ✓ |
| **limit structural complexity** | 파서·리듀서·라우터·서비스를 작은 단일책임 단위로 분리, 현재 테스트 소스 69개(앱 36, Core 31, WindowsAudio 1, LinuxAudio 1)가 개별 타깃을 검증 | tests/ | ✓ |

### 사용성·이식성 (Usability / Portability)

| Tactic | 적용 방식 | 근거 | |
|---|---|---|---|
| **pause/resume** (Usability) | `WorkerPauseGate`(ManualResetEventSlim + Volatile)가 워커 루프를 50ms 슬라이스로 멈추되 정지 요청에는 즉시 반응 | `WorkerPauseGate.cs` | ✓ |
| **defer binding** (Portability) | RID(`win-x64`/`linux-arm64`)에 따라 Platform 참조·`DefineConstants`를 조건부로 바인딩 → 같은 소스로 OS별 앱 생성 | `TimeGrapher.App.csproj` | △ |

---

## 2. Design Patterns

| Pattern | 적용 방식 | 근거 | |
|---|---|---|---|
| **Layers** | App / Platform.* / Core 3계층, 하향 의존만 + CI 강제 | `TimeGrapher.App.csproj` | ✓ |
| **Adapter** | `AudioCaptureWorker`가 NAudio를, `LinuxLiveAudioWorker`가 pw-record/arecord를 `ILiveAudioWorker`로 변환 | Platform.* | ✓(Win) / △(Linux: 프로세스 오케스트레이션 성격) |
| **Factory** | `LiveAudioBackend.CreateWorker`, `IRecordingWriterFactory`, `InfoTabRegistry`(kind→factory 딕셔너리) | 다수 | ✓ |
| **Strategy** | 탭별 frame consumer `IAnalysisFrameConsumer`를 `TabId`로 선택하고, 입력 모드 `IAudioInputWorker`를 동일하게 구동. Positions 탭은 포지션 버튼 렌더러와 시퀀스 렌더러를 하나의 consumer로 묶어 같은 라우팅 계약을 따른다 | `IAnalysisFrameConsumer.cs`, `TestPositionsFrameConsumer.cs` | ✓ |
| **Command** | `RelayCommand`/`AsyncRelayCommand`(ICommand) — 재진입 차단 + CanExecute 재질의 | `AsyncRelayCommand.cs` | ✓ |
| **Observer** | 워커 이벤트(`DataReady`, `AnalysisFrameReady`, `CaptureEnded`) 구독·정지 시 해제 | `AnalysisWorker.cs` | ✓ (브로커형 Pub-Sub은 아님) |
| **Producer-Consumer (bounded)** | 분석→`WavWriter` 스레드를 `BlockingCollection`으로 분리 | `QueuedWavStreamWriter.cs` | ✓ |
| **Shared-Data** | `MasterAudioBuffer`(단일 writer/reader 동기화 링버퍼) | `MasterAudioBuffer.cs` | ✓ (Blackboard 아님) |
| **MVVM** | VM이 바인딩 상태·ICommand 보유, XAML은 로직 없음 | `MainWindowViewModel.cs` | △ |
| **Pipe-and-Filter** | HPF→Envelope→Delay→Detector 단계형 데이터플로 | `TgDetector.cs` | △ |
| **Map-Reduce** | — | — | ✗ 기각 |

---

## 3. 적용도에 대한 정직한 평가 (채점 포인트)

검증 단계에서 **단어만 비슷한 과잉 주장**을 다음과 같이 교정했다. 이 구분 자체가 SAP 학습의 핵심이다.

- **MVVM (△):** 바인딩·커맨드는 진짜지만, **시작/중지 생명주기가 아직 code-behind**(`MainWindow.RunLifecycle.cs`)에 있고 서비스가 VM 상태를 직접 변경한다 → "실용적 부분 MVVM". 발표 자료도 이를 인정.
- **Pipe-and-Filter (△):** 단계 구조는 맞지만 **단일 스레드 동기 호출 체인**이다. 진짜 동시 파이프 경계는 두 곳뿐 — `입력→링버퍼→분석`, `분석→녹음 큐`.
- **defer binding (△):** RID **빌드/배포 시점** 바인딩이라, 교과서가 강조하는 런타임 플러그인/지연 로딩(가장 늦은 바인딩)은 아니다. 카탈로그에서 가장 약한 바인딩 시점.
- **Map-Reduce (✗ 기각):** 분할·병렬·셔플이 전혀 없는 **증분 슬라이딩-윈도우 집계**일 뿐 → `reduce overhead` tactic으로 봐야 한다.
- **기타 교정:**
  - "bound execution times"는 과거 정지 join의 **대기 상한(2초)**뿐이었다. 이후 `AnalysisDeadlineMonitor`가 비트 주기 기반 백로그 감시와 점진 저하를 추가했다 — 단일 패스의 시간 상한은 여전히 없고(4096샘플 청크로 작업 **양**만 바운드), 마감은 사후 감시 + 저하로 다룬다.
  - "retry"는 자동이 아니라 **사용자가 다시 누르면 멱등 재시도**다.
  - PipeWire→ALSA는 fault-recovery `reconfiguration`이 아니라 장치 열거/선택 단계의 **`degradation` 폴백**이다.
  - stale 콜백 폐기는 `ignore faulty behavior`가 아니라 `timestamp`의 stale 탐지 절반이다.

---

## 4. 가장 인상적인 설계 3가지 (발표 권장)

1. **CI로 강제되는 의존성 경계** — 아키텍처 규칙을 문서가 아닌 *실패하는 테스트*로 못박았다(architecture fitness function). `.github/workflows/ci.yml`이 Core의 OS 의존을 grep으로 차단하고, OS별 산출물에 잘못된 DLL이 섞이면 실패시킨다.
2. **"최신 프레임만" 렌더 + 활성 탭만 렌더** — 실시간 UI 멈춤을 막는 성능 tactic 집합(`limit event response` + `schedule resources` + `reduce overhead`). pause 종료 시 리뷰 커서 제거를 위한 저장 프레임 재렌더만 명시적 예외다.
3. **단조 run-session token** — 실시간 시작/중지의 stale-response 버그를 구조적으로 차단한다(`timestamp` tactic).

---

## 5. 원본(Qt/C++) 대비 성능 tactic·패턴 비교

> 2026-06-09, 원본 Qt/C++ TimeGrapher와 이 포팅본을 **비트당 125 ms 예산** 관점에서
> 양쪽 코드 file:line 대조로 분석한 결과다(주장 27건 전수 검증). 비교에서 발견된
> 포팅 회귀 6건은 `perf(imaging)`/`perf(analysis)`x2/`perf(detection)`/`perf(shared)`/
> `perf(rendering)` 커밋으로 수정했고, 마감 강제는 `feat(analysis)` 커밋으로
> 추가했다. **아래 표는 수정 반영 후 상태다.**

### 5.1 SEI 성능 tactic 비교

| SEI Tactic | 원본 (Qt/C++) | 포팅본 (C#) |
|---|---|---|
| **introduce concurrency** | 부분 — 캡처만 별도 QThread. 검출 + 플롯 + WAV 쓰기는 전부 GUI 스레드(`MainWindow.cpp:901-1027`) → UI 지연이 곧 검출 지연 | **완전** — AnalysisWorker 전용 스레드(Highest) + WavWriter 스레드 + UI 3계층. **포팅 최대 개선**: UI가 느려져도 검출은 영향 없음 |
| **limit event response** | 페인트 합류만(QCustomPlot `rpQueuedReplot`) | 단일 슬롯 latest-wins 프레임 합류 + 탭별 33/100 ms 스로틀 + 일회성 신호(오버런)는 병합으로 보존(`AnalysisFrameRenderScheduler.cs`) |
| **bound resource usage** | 그래프가 10초 = 48만 포인트 보유, 매초 50–100회 **전체 컨테이너 rescale**, ~5초마다 24만 포인트 purge 스파이크 | 생산 측에서 8000/250 포인트로 데시메이션 — 원본의 최악 비용 2개가 구조적으로 소멸. **1시간째 비용 = 1초째 비용** |
| **bound queue sizes** | 링은 무음 랩 — 드롭 계측 없음, 30초 이상 밀리면 손상된 타임라인을 감지 없이 읽음 | 모든 버퍼 바운드 + 드롭 정책 명시·계측(링 / WAV 큐 128 / 렌더 큐 1) |
| **schedule resources** | Windows에서 프로세스 전체 `REALTIME_PRIORITY_CLASS` + `timeBeginPeriod(1)` (포팅본엔 없음) | 스레드 우선순위 사다리 + **일반 프레임은 활성 탭만 렌더링**(Strategy 라우팅). pause 종료 커서 제거 때만 `RenderToAll`이 저장 프레임을 1회 재렌더한다. ⚠ .NET `Thread.Priority`는 **Linux에서 no-op** — Pi에서 우선순위는 무효과이며 실제 보호 장치는 바운드 큐 구조다 |
| **increase resource efficiency** | O(1) 증분 알고리즘(`RollingLeastSquares`, PLL ~30 flops/event) | 동일하게 충실 포팅 — 세션 길이와 무관하게 비트당 비용 평탄. 마커→컬럼 조회도 O(1)로 개선(원본은 양쪽 다 O(폭) 선형 탐색이던 핫스팟) |
| **bound execution times** | 알고리즘 캡만(C-onset 탐색 ~5 ms 등). 스냅샷 바운드 드레인은 원본에도 있음 | 동일 + 4096청크마다 stop 체크 + `ProcessingElapsedMs` 측정 + **`AnalysisDeadlineMonitor`가 백로그를 비트 주기로 환산해 점진 저하 실행**(위 "실시간 마감 예산" 절) |

### 5.2 성능 관련 디자인 패턴 비교

| 패턴 | 비교 |
|---|---|
| **Producer-Consumer (30초 공유 링)** | 양쪽의 하중 지지 패턴. 포팅본이 추가한 것: 오버런 드롭 계측, 드레인 중 중단 가능, 지연 텔레메트리 |
| **Producer-Consumer (WAV 기록, bounded)** | **원본에 없음** — GUI 스레드에서 동기 블로킹 파일 I/O(`MainWindow.cpp:926`). SD카드 fsync 지연이 비트 예산을 직접 잠식. 포팅본은 ArrayPool + BlockingCollection(128) drop-never-block, 큐 깊이가 디스크 지연 ~10.9초 흡수 |
| **Observer (queued) + 세션 게이팅** | 양쪽 다 알림에 페이로드를 싣지 않음(데이터는 링으로만). 포팅본은 SessionId + 세대 카운터 3중 게이팅으로 이전 런의 늦은 프레임이 새 런 예산을 0 소비 |
| **Active Object / Mediator / Guarded Suspension** | 포팅본이 더 명시적(`RunSessionController`, `WorkerPauseGate` 50 ms 슬라이스) — 핫 상태 단일 라이터 보장, 정지 경로가 UI를 못 잠그게 함 |
| **Pipes-and-Filters (DSP 체인)** | 양쪽 동일한 포크 토폴로지(검출기는 지연 안 된 envelope을 읽음). 비트당 코어의 ~0.05–0.2% — 병렬화 불필요, 동기 체인 유지가 C 원본 대비 검증성 보존 |
| **Double Buffering** | 원본: 동일 스레드라 QImage 하나로 무복사. 포팅본: 스레드가 분리되어 발행 스냅샷이 필요 — **고정 3버퍼 풀 로테이션**으로 정상 상태 할당 0(UI 측은 WriteableBitmap + 스크래치 재사용) |
