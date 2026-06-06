# TimeGrapherNet

Qt/C++ TimeGrapher(`D:\TimeGrapher_Refactoring`)를 **Avalonia + C# (.NET 8)** 로 포팅한
Windows 데스크톱 앱. 시계 틱 오디오에서 비트레이트(BPH), 레이트 오차(s/d), 비트 에러(ms),
진폭(°)을 실시간 추정하고 스코프/레이트 플롯과 폴디드 사운드 이미지로 시각화한다.

## 빌드 / 실행

```powershell
dotnet restore TimeGrapherNet.sln --locked-mode
dotnet build TimeGrapherNet.sln -c Release      # 첫 복원은 사내망에서 ~3분
dotnet test TimeGrapherNet.sln -c Release
dotnet run --project src/TimeGrapher.App        # GUI
dotnet run --project src/TimeGrapher.Verify -c Release -- D:\TimeGrapher_Refactoring\samples
```

## 프로젝트 구성

| 프로젝트 | 내용 |
|---|---|
| `TimeGrapher.Core` | UI/플랫폼 무관 로직 — 검출 코어(tg_* 포트), 메트릭, 사운드 이미지 렌더러, WAV reader/writer, 시뮬레이터, 분석 워커 |
| `TimeGrapher.App` | Avalonia 11.3 UI — MainWindow, 정보 tab catalog/router, ScottPlot 렌더러 |
| `TimeGrapher.Platform.WindowsAudio` | Windows live audio backend — NAudio WaveInEvent capture, Windows endpoint volume helpers |
| `TimeGrapher.Verify` | 헤드리스 검증 콘솔 — 샘플 WAV의 파일명 BPH와 검출 BPH 비교, 전부 일치 시 exit 0 |
| `TimeGrapher.Core.Tests` | xUnit 회귀 테스트 — 합성 시계 신호 검출, WAV writer/reader round-trip |
| `TimeGrapher.App.Tests` | tab catalog/router, 렌더링 data contract, UI payload 축소 회귀 테스트 |

기술 매핑: Qt Widgets→Avalonia, QCustomPlot→ScottPlot.Avalonia, Qt Multimedia→플랫폼별
audio backend(현재 Windows는 NAudio WaveInEvent), QImage→PixelBuffer(ARGB32)→WriteableBitmap,
QThread/signal→전용 Thread + AutoResetEvent + `Dispatcher.UIThread.Post`. WPF 미사용.

Core는 WindowsAudio/NAudio를 참조하지 않는다. Raspberry Pi/Linux live audio는 별도 platform
backend를 추가하는 방식으로 구현한다.

패키지 버전은 `Directory.Packages.props`에서 중앙 관리하고 `packages.lock.json`을 커밋한다.
CI는 `dotnet restore --locked-mode`, Release build, test, generated/edge WAV verifier,
Windows publish와 publish smoke를 수행한다.

Windows publish artifact는 현재 framework-dependent(`--self-contained false`)이다. 실행 환경에는
.NET 8 Desktop Runtime이 필요하다. 외부 배포용으로 런타임 설치 전제를 없애려면 self-contained
publish artifact를 별도로 만든다.

## 검증 상태 (2026-06-04)

- `dotnet build` 오류 0
- `dotnet test` 통과
- 헤드리스 검증: 샘플 9/9 BPH 정확 검출 (18000/21600/28800), sync 전부 Synced,
  레이트 ±20 s/d 이내, 진폭 206–320°, 비트에러 0.0–1.3 ms
- GUI 스모크: 실행 후 장치 열거/기본값 로드 정상, 크래시 없음

## 원본과의 의도적 차이 (요약)

- `setRenderSource`의 QObject 동적 프로퍼티 기록(런타임 진단용)은 Avalonia 대응 개념이 없어 생략
- QCP 마커 화살촉 글리프는 단순 선분으로 근사 (위치/길이는 동일)
- 스플래시 화면 생략, LinuxAudio(ALSA) 미포팅 (Windows 타겟)
- AGC 비활성화(WindowsAudio.cpp의 device-topology 순회)는 NAudio 미노출로 엔드포인트 볼륨 설정만 수행

세부 편차는 각 소스 파일 주석과 PORTING.md 참조.
