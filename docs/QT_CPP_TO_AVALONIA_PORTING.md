# Qt/C++에서 Avalonia/.NET으로 전환한 과정

이 문서는 기존 Qt + C++ TimeGrapher를 현재의 Avalonia + .NET 8 구조로 옮긴 과정을 설명한다.
목표는 기술 목록을 나열하는 것이 아니라, 왜 이런 구조가 되었는지 흐름을 이해하게 하는 것이다.

## 한 줄 요약

기존 Qt 앱의 계산 로직은 .NET Core 라이브러리로 옮기고, 화면은 Avalonia로 다시 만들고, Windows와 Raspberry Pi의 오디오 입력은 플랫폼별 구현으로 분리했다.

## 왜 전환했나

원본 앱은 Qt Widgets, CMake, C++ 코드로 구성되어 있었다. Windows에서는 동작하지만, Raspberry Pi까지 같은 앱 구조로 가져가려면 UI, 오디오 입력, 배포 방식을 다시 정리해야 했다.

전환의 핵심 목표는 세 가지였다.

1. 같은 분석 로직을 Windows와 Raspberry Pi에서 함께 사용한다.
2. UI가 멈추지 않도록 오디오 분석과 화면 표시를 분리한다.
3. Pi에는 WPF를 쓰지 않고, Linux ARM64에서 실행 가능한 배포물을 만든다.

## 먼저 확인한 것

전환을 시작하기 전에 원본 Qt/C++ 프로젝트를 먼저 읽었다.

- Qt Widgets 기반 UI가 어떤 화면과 버튼 흐름을 갖는지 확인했다.
- QCustomPlot으로 그리던 그래프가 어떤 데이터를 표시하는지 확인했다.
- Qt Multimedia와 `WindowsAudio.cpp`가 라이브 오디오 입력과 Windows 장치 설정을 어떻게 처리하는지 확인했다.
- 샘플 WAV 파일이 48 kHz, mono, 32-bit float 형식이라는 것을 확인했다.
- 검출, 메트릭, 사운드 이미지 렌더링처럼 UI와 분리 가능한 계산 로직을 구분했다.

이 단계에서 중요한 판단은 "그냥 화면 쪽에서 전부 처리하면 안 된다"는 점이었다. 오디오 데이터 양만 보면 작아 보여도, 필터링, 검출, 그래프 갱신, 사운드 이미지 렌더링까지 UI 스레드에서 처리하면 화면이 밀리거나 멈출 수 있다.

## 새 구조를 먼저 잡았다

현재 솔루션은 역할별로 나뉜다.

| 프로젝트 | 역할 |
|---|---|
| `TimeGrapher.Core` | 검출, 메트릭, 사운드 이미지, WAV 처리, 시뮬레이터, 분석 워커 |
| `TimeGrapher.App` | Avalonia UI, 화면 전환, 입력 선택, 그래프 표시, 스플래시 |
| `TimeGrapher.Platform.WindowsAudio` | Windows 라이브 오디오 입력과 장치 볼륨 제어 |
| `TimeGrapher.Platform.LinuxAudio` | Raspberry Pi/Linux 라이브 오디오 입력 (PipeWire `pw-record` / ALSA `arecord`) |
| `TimeGrapher.Verify` | 화면 없이 샘플 WAV를 분석하는 검증 콘솔 |
| 테스트 프로젝트 | 계산 로직, 렌더링 계약, UI 데이터 전달 회귀 테스트 |

이렇게 나눈 이유는 단순하다.

- Core는 Windows, Linux, Pi를 몰라도 된다.
- App은 화면과 사용자 조작을 담당한다.
- 실제 오디오 장치 처리는 운영체제별 구현 뒤에 숨긴다.
- Verify는 GUI 없이도 포팅 결과가 맞는지 확인한다.

## 기술을 어떻게 대응시켰나

원본의 기술 요소를 .NET 쪽 기능으로 그대로 옮기되, 1:1 복사가 아니라 같은 역할을 하는 구조로 바꿨다.

| Qt/C++ 원본 | Avalonia/.NET 쪽 대응 |
|---|---|
| Qt Widgets | Avalonia XAML과 C# 코드 |
| QCustomPlot | ScottPlot.Avalonia와 전용 렌더링 코드 |
| Qt Multimedia | Windows는 NAudio, Pi/Linux는 PipeWire와 ALSA 실행 |
| QThread와 signal | 백그라운드 작업, 이벤트, 화면 주 스레드 전달 |
| QImage | PixelBuffer와 WriteableBitmap |
| C++ 검출/계산 코드 | `TimeGrapher.Core`의 C# 계산 코드 |
| Qt 동영상 스플래시 | MP4에서 뽑은 PNG 74장을 30fps로 재생 |

특히 WPF는 사용하지 않았다. WPF는 Windows 전용이라 Raspberry Pi 목표와 맞지 않기 때문이다.

## 계산 로직은 Core로 옮겼다

시계 틱을 분석하는 핵심은 UI가 아니라 계산 로직이다. 그래서 다음 기능을 `TimeGrapher.Core`로 옮겼다.

- 시간당 박동 수(BPH) 검출
- 하루 오차, 틱/톡 간격 차이, 진폭 계산
- rolling average와 least squares 계산
- WAV 읽기/쓰기
- 소리 이미지 생성
- 합성 시계 신호 생성
- 분석 작업

Core가 UI와 플랫폼을 모르게 만든 덕분에, Windows GUI 없이도 샘플 WAV를 분석하고 테스트할 수 있다.

## 화면은 Avalonia로 다시 만들었다

화면은 Qt 위젯을 그대로 흉내 내기보다, 같은 기능을 Avalonia 방식으로 다시 구성했다.

- `MainWindow`가 실행 모드 선택, 시작/중지, 탭 표시를 담당한다.
- 그래프와 사운드 이미지는 화면이 감당할 수 있는 속도로 갱신한다.
- 모든 오디오 샘플을 매번 화면에 그리지 않고, 화면에 필요한 최신 결과만 넘긴다.
- `SplashWindow`는 640x360 PNG 시퀀스를 먼저 보여준 뒤 `MainWindow`로 전환한다.

이 구조의 목적은 보기 좋은 코드가 아니라, 실행 중 멈추지 않는 UI다.

## 오디오 입력은 플랫폼별로 분리했다

오디오 입력은 운영체제마다 방식이 다르다. 그래서 공통 UI에서 직접 장치를 다루지 않고, 작은 입력 계약 뒤에 숨겼다.

Windows에서는 NAudio 기반 입력을 사용한다.

- `WaveInEvent`로 라이브 입력을 받는다.
- Windows 장치 볼륨 제어는 별도 플랫폼 프로젝트에 둔다.

Raspberry Pi와 Linux에서는 외부 명령을 이용한다.

- 먼저 PipeWire source를 확인하고 `pw-record`로 raw float mono stream을 받는다.
- PipeWire source가 없으면 ALSA capture hardware를 확인하고 `arecord`로 raw S16 mono stream을 받는다.
- capture source가 없으면 live input은 비활성화하고 Playback/Sim 모드는 계속 사용할 수 있게 한다.

이렇게 한 이유는 Pi에서 Windows 오디오 API나 WPF에 기대지 않기 위해서다.

## 스레드 구조를 바꾼 이유

원본 Qt 앱에도 백그라운드 작업 개념이 있었고, .NET 쪽에서도 그 생각을 유지했다.

현재 흐름은 다음과 같다.

```text
오디오 입력 / WAV / 시뮬레이터
        ↓
분석 작업
        ↓
검출 결과와 화면용 데이터
        ↓
Avalonia 화면 주 스레드
```

화면 주 스레드는 버튼, 탭, 그래프 표시를 담당한다. 오디오 분석, 검출, 이미지 계산은 백그라운드 작업에서 처리한다. 이 경계를 지키면 Raspberry Pi처럼 성능 여유가 적은 환경에서도 화면이 덜 밀린다.

원본과 포팅본을 성능 tactic·디자인 패턴 차원에서 정량 비교한 결과(비트당 125 ms 예산 기준)는 `SAP_TACTICS_ANALYSIS.md`의 "실시간 마감 예산" 절과 5절에 정리되어 있다.

## Raspberry Pi 대응

Pi용 배포는 `linux-arm64` self-contained publish를 사용한다.

```powershell
dotnet publish .\src\TimeGrapher.App\TimeGrapher.App.csproj -c Release -r linux-arm64 --self-contained true
```

Pi에서는 다음을 확인했다.

- ARM64 Linux에서 실행 가능한 publish 생성
- `./TimeGrapher.App --smoke` 통과
- `DISPLAY=:0` GUI 실행이 스플래시 시간보다 오래 유지됨
- XWayland/Avalonia 실행에 필요한 기본 GUI 라이브러리 확인

현재 남은 물리 검증은 실제 USB microphone 또는 capture source를 연결한 뒤 live input을 확인하는 것이다.

## 검증을 어떻게 했나

전환 과정에서는 GUI 실행만 믿지 않고, 단계별로 검증했다.

| 검증 | 의미 |
|---|---|
| `dotnet build` | 전체 솔루션이 컴파일되는지 확인 |
| `dotnet test` | 계산 로직과 UI 데이터 전달 계약 확인 |
| `TimeGrapher.Verify` | 샘플 WAV에서 기대 BPH가 검출되는지 확인 |
| Windows smoke | 앱 시작 경로가 깨지지 않았는지 확인 |
| Pi smoke | ARM64 배포물이 Pi에서 실행되는지 확인 |
| Pi GUI 유지 확인 | Avalonia 창이 실제 화면 환경에서 바로 죽지 않는지 확인 |

최근 기준으로 Windows 빌드, 자동 테스트, 앱 smoke, Pi publish, Pi smoke, Pi GUI 유지 확인까지 통과했다.

## 전환 과정에서 일부러 다르게 만든 것

원본과 완전히 똑같이 복사하지 않은 부분도 있다.

- Qt의 동적 프로퍼티 기록은 Avalonia에 대응되는 개념이 없어 생략했다.
- Qt 동영상 스플래시는 MP4 직접 재생 대신 PNG 시퀀스 재생으로 바꿨다.
- Windows 장치의 AGC 비활성화 경로는 NAudio에서 직접 노출되지 않아 엔드포인트 볼륨 제어 중심으로 정리했다.
- Pi live audio는 Windows와 같은 라이브러리가 아니라 PipeWire/ALSA 명령 기반으로 구현했다.
- 원본의 Linux 마이크 초기화(`LinuxSetSoundParameters`: ALSA 캡처 볼륨 50% 설정 + AGC 비활성화)는 아직 포팅하지 않았다 — Pi에서는 마이크 볼륨/AGC가 시스템 기본값으로 동작하며, ALSA 믹서 제어(`amixer`) 기반 이식은 Pi 실기 검증과 함께 진행할 항목으로 남겨 두었다.

이 차이는 기능을 포기하기 위한 것이 아니라, Windows와 Raspberry Pi를 모두 지원하기 위해 현실적인 방식으로 바꾼 것이다.

## 검출 강건성 옵션 — 포팅 이후의 자체 진화

원본 Qt/C++ 소스는 임시 제공본으로 권위가 없으며, **"원본 보존"은 더 이상 이 저장소의 원칙이 아니다**(소유자 결정). 검출기는 측정 근거에 따라 자체적으로 진화한다. 약신호/고노이즈 환경 검출력 개선은 이제 옵션이 아니라 Core 기본 검출 동작이다. 적응 플로어, 레짐 가드, PLL-guided post-lock A-onset gating / min-peak sensitivity는 기준선 보존 경로 없이 항상 적용되며, null/all-off 동등성 검증과 `--fidelity-check`는 제거했다. PLL 이벤트 베토만 GUI 체크박스 뒤 옵트인으로 남아 추가 메트릭 보호 축으로 측정된다.

- `EnableAdaptiveFloor` — 원본은 기각 버스트가 아무 통계도 남기지 않아 10×노이즈 플로어 아래의 약한 시계가 영구 검출 불가였다(수락이 없으면 기준 중앙값이 하향 적응할 수 없는 구조). 옵션은 기각-피크 섀도 중앙값으로 플로어를 하향 적응시키고, 수락 공백 후 기준 피크를 지수 감쇠 + 히스토리 재시작해 큰소리→조용함 래치를 푼다(V5.6 상향 레짐 리셋의 하향 대칭).
- `EnableRegimeGuard` — 원본 V5.6의 순간 레짐 트립은 단발 임펄스(문 쾅) 하나로 검출기 적응 상태·BPH 락·PLL을 전부 플러시한다. 옵션은 run-of-3 지속 카운터로 디바운스한다.
- `TrackEventPllMatch` + `IBeatEventGate`(`PllMatchGate`) — 원본은 동기 후 모든 이벤트를 PLL 매치 여부와 무관하게 메트릭에 전달한다. 옵션은 위상 매치에 실패한 이벤트를 메트릭 직전에 베토한다(검출기 출력 자체는 불변).

이 동작은 Verify의 악조건 행(`--adverse`)으로 측정·게이트된다. 무음 선행 후 정상 신호가 들어오는 bootstrap 행은 pre-lock regime trip이 BPH 획득 히스토리를 지우지 않도록 처리해 게이트한다. 약신호(`weak-*`)와 지속 잡음(`noisy-*`)의 default arm도 event-level recall/precision gate로 검증하며, `--gate=pll` arm은 기본 품질 게이트가 아니라 옵트인 베토 효과를 INFO로 비교하는 축이다.

## 결과적으로 얻은 구조

전환 후 앱은 다음 구조가 되었다.

```text
TimeGrapher.App
  Avalonia UI
  입력 모드 선택
  그래프/탭/스플래시

TimeGrapher.Core
  검출/메트릭/이미지/WAV/시뮬레이션
  UI와 운영체제에 의존하지 않음

Platform 구현
  Windows: NAudio와 Windows 장치 제어
  Pi/Linux: PipeWire와 ALSA 입력

검증 도구와 테스트
  GUI 없이 계산 결과 확인
  Windows/Pi 실행 확인
```

핵심은 "Qt를 Avalonia로 바꿨다"가 아니다. 핵심은 계산, 화면, 플랫폼 입력을 나눠서 Windows와 Raspberry Pi에서 같은 분석 앱을 실행할 수 있게 만든 것이다.
