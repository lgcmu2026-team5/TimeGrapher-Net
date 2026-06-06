# TimeGrapher Architecture Improvement Notes

CMU-LG Software Architecture Training Course 발표용 정리.

원본 프로젝트는 Qt + C++ 데스크톱 앱이었다. 현재 프로젝트는 Avalonia + C#/.NET 8로 포팅하면서,
단순 언어 변환이 아니라 실시간 오디오 분석 앱에 필요한 구조를 다시 정리한 결과물이다.

## 1. 발표 핵심 메시지

TimeGrapher는 "소리를 받아서 화면에 그리는 앱"처럼 보이지만, 실제 핵심은 실시간 파이프라인이다.

발표에서 내세울 한 문장은 다음이다.

> UI가 모든 일을 하는 구조에서, 입력/분석/렌더링의 책임과 속도를 분리한 구조로 바꿨다.

이 변화의 목적은 세 가지다.

- UI가 멈추지 않게 한다.
- 분석 결과가 밀리거나 섞이지 않게 한다.
- 새 화면, 새 입력 장치, 새 테스트를 추가하기 쉽게 한다.

## 1-1. 발표 표현 가이드

코드 상태와 발표 문구가 어긋나지 않게 아래 표현을 기준으로 한다.

| 피할 표현 | 사용할 표현 |
|---|---|
| MVVM 전면 전환 완료 | 실용적 MVVM 적용 |
| MainWindow 책임 제거 | MainWindow 책임 축소 |
| 완전한 plugin 구조 | Core 분리와 backend selector 구조 |
| 모든 frame을 안전하게 처리 | UI는 최신 snapshot 중심으로 안전하게 렌더링 |

특히 MVVM은 이렇게 말하는 것이 정확하다.

> ViewModel, command service, selection coordinator를 도입해 UI 상태와 명령 처리를 분리했다.
> 다만 worker lifecycle과 UI frame scheduling은 아직 MainWindow가 조정하므로, 전면 MVVM이 아니라 실용적 MVVM 적용이다.

## 2. Before / After

| 관점 | 기존 Qt/C++ 포팅 직후 | 현재 개선 방향 |
|---|---|---|
| 구조 | MainWindow 중심, UI 코드가 많은 결정을 직접 수행 | Core / App / Platform 경계 분리 |
| 실시간 처리 | frame이 생길 때마다 UI에 전달될 위험 | 최신 frame coalescing + 탭별 refresh 정책 |
| 그래프 | UI frame마다 plot 재생성 가능성 | plottable 유지, 데이터만 교체 |
| 탭 확장 | 화면별 로직이 MainWindow에 모일 위험 | InfoTabCatalog + Router + Consumer |
| 플랫폼 의존성 | 오디오 입력 구현이 분석 코드와 섞일 위험 | Windows/PipeWire/ALSA backend를 App 경계 뒤로 이동 |
| 설정값 | BPH, sample rate 등 배열/인덱스 직접 접근 | catalog/resolver로 의미 있는 값과 검증을 분리 |
| 테스트 | GUI 실행 확인 위주 | Core/App 단위 테스트 + headless verifier |

## 3. SAP 관점의 품질 속성

### Performance: 실시간성을 지키기

문제는 raw audio 양 자체보다 UI thread에서 너무 많은 일을 하는 것이다.
48~96 kHz mono audio는 처리 가능하지만, 필터링, 검출, 그래프 갱신, 이미지 복사를 UI에서 같이 하면 앱이 멈춘다.

적용한 tactic:

- 입력 worker, 분석 worker, UI rendering을 분리했다.
- UI는 모든 frame을 그리지 않고 최신 frame 중심으로 coalescing한다.
- Sound Print 이미지는 매 frame 복사하지 않고 publish cadence를 제한한다.
- 그래프는 매번 새로 만들지 않고 기존 plottable의 데이터만 교체한다.
- 표시점 수는 target point budget으로 줄인다.

쉽게 말하면, "계산은 worker가 하고, UI는 보여줄 만큼만 그린다."

### Modifiability: 새 기능을 붙이기 쉽게 하기

정보 탭은 앞으로 늘어날 수 있다. 그래서 `MainWindow`가 모든 탭을 직접 알게 하지 않았다.

적용한 tactic:

- `InfoTabCatalog`가 탭 ID, 제목, refresh 정책을 가진다.
- `InfoTabRegistry`가 catalog를 보고 실제 UI 탭과 consumer를 만든다.
- `AnalysisFrameRouter`가 현재 활성 탭만 비싼 rendering을 수행하게 한다.
- `RateScopeFrameConsumer`, `SoundPrintFrameConsumer`가 탭별 처리를 나눠 가진다.

새 탭을 추가할 때는 "MainWindow를 고치는 일"보다 "catalog와 consumer를 추가하는 일"에 가까워진다.

### Reliability: 밀려도 깨지지 않는 데이터 계약

UI frame coalescing은 성능에는 좋지만 위험도 있다. 오래된 frame을 버릴 때, frame-local append 데이터도 같이 사라질 수 있기 때문이다.

해결:

- Scope/Rate graph payload를 append가 아니라 replace snapshot 계약으로 바꿨다.
- 분석 worker가 보이는 구간의 bounded snapshot을 유지한다.
- frame에는 lag, pending samples, dropped samples 같은 backpressure 상태를 포함한다.
- playback/sim/live stop 경로에 timeout join과 session token을 둔다.

즉, UI가 조금 늦어도 "중간 append를 놓쳐서 그래프가 깨지는" 구조를 피했다.

### Portability: 플랫폼 차이를 경계 뒤로 숨기기

Core는 시계 소리 분석 로직이다. Windows audio, PipeWire, ALSA는 Core가 알 필요가 없다.

적용한 tactic:

- `TimeGrapher.Core`: 검출, 메트릭, WAV, sim, analysis worker
- `TimeGrapher.App`: Avalonia UI, tab routing, live audio backend 선택
- `TimeGrapher.Platform.WindowsAudio`: Windows NAudio capture와 endpoint volume
- Linux/Pi capture는 PipeWire `pw-record`, fallback은 ALSA `arecord`

이 구조 덕분에 Core는 Windows 없이도 테스트할 수 있고, 플랫폼 입력은 교체 가능하다.

### Testability: GUI 없이 검증 가능하게 하기

실시간 GUI 앱은 화면으로만 검증하면 회귀를 잡기 어렵다.

적용한 tactic:

- `TimeGrapher.Verify`로 WAV 파일을 headless 분석한다.
- generated/byte fixture WAV로 edge case를 검증한다.
- Core tests는 detector, WAV, worker pause, frame contract를 확인한다.
- App tests는 tab catalog, router, selection, run command service를 확인한다.

현재 기준 전체 테스트는 Core 31개, App 49개가 통과한다.

## 4. 사용한 아키텍처/디자인 패턴

| 패턴 | 이 프로젝트에서의 형태 | 효과 |
|---|---|---|
| Layered Architecture | Core / App / Platform 분리 | 분석 로직과 UI/OS 의존성 분리 |
| Ports and Adapters | `ILiveAudioWorker`, `LinuxLiveAudioWorker`, backend selector | Windows/Pi 입력 방식 교체 가능 |
| Producer-Consumer | audio worker -> buffer -> analysis worker -> UI | 실시간 입력과 분석 속도 분리 |
| Observer/Event | `DataReady`, `AnalysisFrameReady` | thread 간 notify-only 연결 |
| Practical MVVM | `MainWindowViewModel`, command service, selection coordinator | UI 상태와 command enable/disable 분리 |
| Catalog + Factory | `InfoTabCatalog`, `InfoTabRegistry` | 탭 추가 지점을 명확히 함 |
| Router | `AnalysisFrameRouter` | 활성 탭만 렌더링 |
| Strategy-like input lifecycle | `IAudioInputWorker` + Live/Playback/Sim worker | 입력 source별 lifecycle 제어 통일 |

여기서 중요한 점은 패턴 이름이 아니라, 각 패턴이 실제 문제를 줄였다는 것이다.

## 5. 대표 개선 사항

### 5.1 Core 분리

Core는 UI도, Windows도, PipeWire도 모른다. 분석에 필요한 순수 로직만 가진다.

발표 포인트:

- 테스트 가능성이 커졌다.
- 플랫폼 변경의 영향 범위가 줄었다.
- 알고리즘 코드를 UI 코드와 따로 읽을 수 있다.

### 5.2 실용적 MVVM 적용

완전한 MVVM 프레임워크를 도입하지는 않았다. 대신 효과가 큰 부분부터 분리했다.

- `MainWindowViewModel`: UI 상태와 command enable/disable
- `RunCommandService`: Start/Pause/Stop 흐름
- `MainWindowSelectionCoordinator`: mode/device/sample-rate 선택 side effect
- `PlaybackFileService`, `RecordingSessionService`: dialog가 섞인 workflow 분리
- `RunSelectionResolver`: 선택 인덱스를 실제 값으로 검증해서 변환

발표 포인트:

> 이 프로젝트에서는 "완벽한 패턴 적용"보다 "MainWindow가 너무 많은 결정을 하지 않게 하는 것"이 목표였다.

### 5.3 Canvas layout 재설계

처음에는 Qt `.ui`의 absolute layout을 최대한 보존하는 것이 중요했다.
하지만 발표 가능한 개선 기준에서는 오른쪽 정보 영역이 늘어날 가능성이 더 중요했다.

개선 방향:

- 왼쪽 control panel은 기존 사용 흐름을 유지한다.
- 오른쪽 results/tab 영역은 resize 가능한 Grid 중심으로 바꾼다.
- 탭 내부 control은 XAML에 고정 선언하지 않고 registry/catalog가 만든다.

발표 포인트:

> 사용자가 조작하는 왼쪽은 안정적으로 유지하고, 확장 가능성이 큰 오른쪽 정보 영역만 구조화했다.

### 5.4 실시간 UI tactic

실시간 앱에서 가장 중요한 것은 "빨리 계산하기"만이 아니다.
UI가 감당할 수 있는 속도로 보여주는 것도 중요하다.

적용한 방법:

- analysis frame을 UI queue에 무한히 쌓지 않는다.
- 최신 frame 하나를 중심으로 rendering한다.
- inactive tab은 관찰만 하고 expensive rendering은 하지 않는다.
- scope/rate series는 snapshot replace 계약으로 전달한다.

### 5.5 lifecycle 안정화

worker가 제대로 멈추지 않으면 다음 run에서 이전 session의 이벤트가 섞일 수 있다.

개선:

- run session token으로 오래된 callback을 무시한다.
- stop은 timeout join으로 UI thread를 무기한 붙잡지 않는다.
- playback/sim completion에는 completion reason을 둔다.
- Linux capture 재시작 시 기존 process를 dispose만 하지 않고 stop 경로로 정리한다.

## 6. 품질 속성과 코드 근거

| 품질 속성 | 코드 근거 |
|---|---|
| Performance | `AnalysisFrameRouter`, `RateScopeRenderer`, `SeriesDataReducer`, frame coalescing |
| Modifiability | `InfoTabCatalog`, `InfoTabRegistry`, `IAnalysisFrameConsumer` |
| Reliability | `AnalysisFrame` backpressure fields, session token, bounded stop |
| Portability | `TimeGrapher.Core`, `TimeGrapher.Platform.WindowsAudio`, `LinuxLiveAudioWorker` |
| Testability | `TimeGrapher.Verify`, Core/App xUnit tests |
| Usability | run state ViewModel, command enable/disable, responsive layout |

## 7. 발표에서 강조할 trade-off

### 왜 원본을 1:1로만 유지하지 않았나?

포팅 초반에는 원본 동작 보존이 중요했다.
하지만 구조 개선 단계에서는 "원본과 같은 화면"보다 "앞으로 바꾸기 쉬운 구조"가 더 중요했다.

### 왜 full MVVM을 하지 않았나?

이 앱은 실시간 worker와 rendering lifecycle이 중요하다.
모든 코드를 ViewModel로 밀어 넣으면 오히려 thread/lifecycle이 흐려질 수 있다.
그래서 command, selection, run state처럼 효과가 큰 부분부터 분리했다.

### 왜 UI frame을 버려도 되는가?

분석 결과 자체를 버리는 것이 아니다.
UI가 볼 snapshot만 최신 것으로 교체한다.
그래프 계약을 replace snapshot으로 바꿨기 때문에 중간 frame을 그리지 않아도 화면 상태가 깨지지 않는다.

### 왜 Core에서 플랫폼 코드를 빼야 하나?

Windows 마이크 입력과 Raspberry Pi 입력은 다르다.
하지만 "시계 틱을 분석하는 규칙"은 같다.
그래서 Core는 순수 분석에 집중하고, 입력 방식은 App/Platform 경계 뒤에 둔다.

## 8. 발표용 1분 요약

처음 프로젝트는 Qt/C++로 작성된 실시간 시계 소리 분석 앱이었다.
우리는 이를 Avalonia/C#으로 포팅하면서, 단순히 언어만 바꾼 것이 아니라 실시간 앱에 맞는 구조로 재정리했다.

가장 큰 변화는 UI가 모든 일을 직접 처리하지 않도록 한 것이다.
오디오 입력, 분석, UI rendering을 분리했고, UI는 최신 snapshot만 필요한 속도로 그리게 했다.
또한 Core와 Platform을 분리해 Windows, Raspberry Pi 같은 실행 환경 차이가 분석 로직으로 번지지 않게 했다.

디자인 패턴은 목적이 아니라 수단으로 사용했다.
Catalog와 Router는 탭 확장을 쉽게 만들기 위해, MVVM 일부는 UI 상태와 command를 분리하기 위해,
Ports and Adapters는 플랫폼 오디오 입력을 교체하기 위해 사용했다.

결과적으로 성능, 수정용이성, 신뢰성, 테스트 가능성이 모두 좋아졌다.
현재는 GUI 없이도 Core/App 테스트와 headless verifier로 주요 동작을 확인할 수 있다.

## 9. 발표 슬라이드 구성 제안

1. 원본 프로젝트 소개: Qt/C++ TimeGrapher, 실시간 오디오 분석 앱
2. 가장 큰 문제: UI, 분석, 플랫폼 코드가 한쪽으로 몰리기 쉬움
3. 핵심 결정: 입력/분석/렌더링 분리
4. 품질 속성별 개선: Performance, Modifiability, Reliability, Portability, Testability
5. 구조 그림: Core / App / Platform / Verify
6. 탭 확장 구조: Catalog -> Registry -> Router -> Consumer
7. 실시간 tactic: frame coalescing, snapshot contract, rate-limited rendering
8. 실용적 MVVM: ViewModel, RunCommandService, SelectionCoordinator
9. 검증: Core/App tests, verifier, smoke
10. 결론: 패턴 이름보다 품질 속성 개선이 핵심

## 10. 한 장짜리 구조 그림

```text
Live Audio / Playback / Sim
          |
          v
  MasterAudioBuffer
          |
          v
  AnalysisWorker  ---> Detector / Metrics / SoundImage
          |
          v
  AnalysisFrame snapshot
          |
          v
  MainWindow UI scheduler
          |
          v
  AnalysisFrameRouter
      |              |
      v              v
 Rate/Scope       Sound Print
 Consumer         Consumer
```

플랫폼 경계:

```text
TimeGrapher.Core
  - detection, metrics, WAV, sim, analysis
  - no Avalonia
  - no NAudio
  - no PipeWire

TimeGrapher.App
  - Avalonia UI
  - tab routing
  - live audio backend selection

TimeGrapher.Platform.WindowsAudio
  - Windows NAudio capture
  - endpoint volume control
```

## 11. 발표 시 피하면 좋은 표현

- "MVVM을 완벽하게 적용했다"보다는 "효과가 큰 부분부터 실용적으로 분리했다."
- "프레임을 버린다"보다는 "UI가 볼 최신 snapshot으로 교체한다."
- "패턴을 많이 썼다"보다는 "품질 속성을 위해 필요한 구조를 선택했다."
- "C++를 C#으로 바꿨다"보다는 "포팅을 계기로 아키텍처 경계를 명확히 했다."
