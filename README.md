# TimeGrapherNet

Qt/C++ TimeGrapher(`D:\TimeGrapher_Refactoring`)를 **Avalonia + C# (.NET 8)** 로 포팅한
cross-platform 데스크톱 앱. 시계 틱 오디오에서 비트레이트(BPH), 레이트 오차(s/d), 비트 에러(ms),
진폭(°)을 실시간 추정하고 스코프/레이트 플롯과 폴디드 사운드 이미지로 시각화한다.

## 빌드 / 실행

```powershell
dotnet restore TimeGrapherNet.sln --locked-mode
dotnet build TimeGrapherNet.sln -c Release      # 첫 복원은 사내망에서 ~3분
dotnet test TimeGrapherNet.sln -c Release
dotnet run --project src/TimeGrapher.App        # GUI
dotnet run --project src/TimeGrapher.Verify -c Release -- D:\TimeGrapher_Refactoring\samples
```

Raspberry Pi 5 / ARM64 self-contained publish:

```powershell
dotnet publish .\src\TimeGrapher.App\TimeGrapher.App.csproj -c Release -r linux-arm64 --self-contained true
```

Pi GUI 실행에는 XWayland/Avalonia 의존성(`libx11-6`, `libice6`, `libsm6`, `libfontconfig1`,
`xwayland`)이 필요하다. Pi live audio는 먼저 PipeWire source를 `wpctl status`로 열거하고
`pw-record` raw float mono stream을 분석 pipeline에 공급한다. PipeWire source가 없으면
ALSA capture hardware를 `arecord -l`로 열거하고 `arecord` raw S16 mono stream으로 fallback한다.
capture source가 없으면 UI는 `Playback/Sim`만 표시한다.

Pi에서 화면 없이 audio 상태를 확인할 때:

```bash
./TimeGrapher.App --audio-smoke
./TimeGrapher.App --capture-smoke --duration-ms=1500
```

`--audio-smoke`는 PipeWire/ALSA capture source 목록을 출력한다. `--capture-smoke`는 첫 source를
짧게 열고 `samples_written`을 출력하며, source가 없으면 exit code 2를 반환한다.

## 프로젝트 구성

| 프로젝트 | 내용 |
|---|---|
| `TimeGrapher.Core` | UI/플랫폼 무관 로직 — 검출 코어(tg_* 포트), 메트릭, 사운드 이미지 렌더러, WAV reader/writer, 시뮬레이터, 분석 워커 |
| `TimeGrapher.App` | Avalonia 11.3 UI — MainWindow, 정보 tab catalog/router, ScottPlot 렌더러, platform live audio selector |
| `TimeGrapher.Platform.WindowsAudio` | Windows live audio backend — NAudio WaveInEvent capture, Windows endpoint volume helpers |
| `TimeGrapher.Verify` | 헤드리스 검증 콘솔 — 샘플 WAV의 파일명 BPH와 검출 BPH 비교, 전부 일치 시 exit 0 |
| `TimeGrapher.Core.Tests` | xUnit 회귀 테스트 — 합성 시계 신호 검출, WAV writer/reader round-trip |
| `TimeGrapher.App.Tests` | tab catalog/router, 렌더링 data contract, UI payload 축소 회귀 테스트 |

기술 매핑: Qt Widgets→Avalonia, QCustomPlot→ScottPlot.Avalonia, Qt Multimedia→플랫폼별
audio backend(Windows는 NAudio WaveInEvent, Linux/Pi는 PipeWire `pw-record` + ALSA `arecord` fallback), QImage→PixelBuffer(ARGB32)→WriteableBitmap,
QThread/signal→전용 Thread + AutoResetEvent + `Dispatcher.UIThread.Post`. WPF 미사용.

Core는 WindowsAudio/NAudio/PipeWire를 참조하지 않는다. live audio backend는 App의 작은
`ILiveAudioWorker` 계약 뒤에서 선택되고, 실행 중인 Live/Playback/Sim 입력 worker는
공통 `IAudioInputWorker` lifecycle로 pause/stop/data-ready를 처리한다.

패키지 버전은 `Directory.Packages.props`에서 중앙 관리하고 `packages.lock.json`을 커밋한다.
CI는 `dotnet restore --locked-mode`, Release build, test, generated/edge WAV verifier,
Windows publish와 publish smoke를 수행한다.

Windows publish artifact는 현재 framework-dependent(`--self-contained false`)이다. 실행 환경에는
.NET 8 Desktop Runtime이 필요하다. 외부 배포용으로 런타임 설치 전제를 없애려면 self-contained
publish artifact를 별도로 만든다.

## 검증 상태 (2026-06-06)

- `dotnet build` 오류 0
- `dotnet test TimeGrapherNet.sln -c Release` 통과
- `linux-arm64 --self-contained true` publish 통과
- Raspberry Pi 5: `./TimeGrapher.App --smoke` 통과, `DISPLAY=:0` GUI 실행 유지,
  Wayland `grim` 캡처로 UI 렌더링 확인
- 현재 테스트 Pi에는 PipeWire/ALSA capture source가 없어 live microphone 입력은 물리 검증 대기
- 헤드리스 검증: 샘플 9/9 BPH 정확 검출 (18000/21600/28800), sync 전부 Synced,
  레이트 ±20 s/d 이내, 진폭 206–320°, 비트에러 0.0–1.3 ms
- GUI 스모크: 실행 후 장치 열거/기본값 로드 정상, 크래시 없음

## 원본과의 의도적 차이 (요약)

- `setRenderSource`의 QObject 동적 프로퍼티 기록(런타임 진단용)은 Avalonia 대응 개념이 없어 생략
- QCP 마커 화살촉 글리프는 단순 선분으로 근사 (위치/길이는 동일)
- 스플래시 화면 생략
- AGC 비활성화(WindowsAudio.cpp의 device-topology 순회)는 NAudio 미노출로 엔드포인트 볼륨 설정만 수행

세부 편차는 각 소스 파일 주석 참조.
