# Module Decomposition View

이 문서는 TimeGrapherNet 솔루션을 모듈 분해 관점에서 보여준다. 외곽 상자는 상위 모듈이고, 그 안에 배치된 상자들은 해당 모듈에 포함되는 하위 모듈이다.

## Decomposition diagram

```mermaid
flowchart TB
    subgraph Solution["TimeGrapherNet solution"]
        direction TB

        subgraph Src["src - runtime and tool modules"]
            direction TB

            subgraph MainRuntime["application and analysis"]
                direction LR

                subgraph App["TimeGrapher.App<br/>Avalonia desktop application"]
                    direction TB
                    AppStartup["Startup / settings<br/>Program, App.axaml,<br/>AnalysisRunSettings"]
                    AppViews["Views<br/>MainWindow, SplashWindow"]
                    AppViewModels["ViewModels<br/>state, commands"]
                    AppServices["Services<br/>run lifecycle, selection,<br/>recording, dialogs"]
                    AppTabs["Tabs<br/>catalog, registry,<br/>frame routing"]
                    AppRendering["Rendering<br/>frame consumers, plots,<br/>readouts, images"]
                    AppAudio["Audio<br/>backend selection,<br/>smoke check"]
                    AppAssets["Assets<br/>icons, fonts,<br/>splash frames"]

                    AppStartup ~~~ AppViews
                    AppStartup ~~~ AppAudio
                    AppViews ~~~ AppViewModels
                    AppViewModels ~~~ AppServices
                    AppServices ~~~ AppTabs
                    AppTabs ~~~ AppRendering
                    AppRendering ~~~ AppAudio
                    AppAudio ~~~ AppAssets
                end

                subgraph Core["TimeGrapher.Core<br/>analysis engine"]
                    direction TB
                    CoreAnalysis["Analysis<br/>worker, deadline monitor,<br/>FFT, frame projectors,<br/>beat-event gate host"]
                    CoreDetection["Detection<br/>tick/tock, BPH,<br/>sync, filters,<br/>robustness options"]
                    CoreScoring["Detection.Scoring<br/>IBeatEventGate (ML socket),<br/>PLL match gate, window features"]
                    CoreMetrics["Metrics<br/>watch metrics,<br/>rolling/decimating statistics"]
                    CoreImaging["Imaging<br/>sound image renderer"]
                    CoreAudioIo["AudioIo<br/>WAV read/write,<br/>playback worker"]
                    CoreSim["Sim<br/>synthetic watch signal,<br/>detection scorer"]
                    CoreShared["Shared<br/>contracts, buffers,<br/>frame DTOs"]

                    CoreAnalysis ~~~ CoreDetection
                    CoreDetection ~~~ CoreScoring
                    CoreScoring ~~~ CoreMetrics
                    CoreMetrics ~~~ CoreImaging
                    CoreImaging ~~~ CoreAudioIo
                    CoreAudioIo ~~~ CoreSim
                    CoreSim ~~~ CoreShared
                end

                App ~~~ Core
            end

            subgraph RuntimeSupport["platform and verification"]
                direction LR

                subgraph Platform["platform audio projects"]
                    direction TB
                    WindowsAudio["TimeGrapher.Platform.WindowsAudio<br/>NAudio capture and system audio"]
                    LinuxAudio["TimeGrapher.Platform.LinuxAudio<br/>wpctl/pw-record/arecord capture"]

                    WindowsAudio ~~~ LinuxAudio
                end

                Verify["TimeGrapher.Verify<br/>headless generated/WAV verification"]

                Platform ~~~ Verify
            end
        end

        subgraph QualityAndSupport["tests and supporting artifacts"]
            direction LR

            subgraph Tests["tests"]
                direction TB
                AppTests["TimeGrapher.App.Tests<br/>UI support, services,<br/>rendering tests"]
                CoreTests["TimeGrapher.Core.Tests<br/>analysis, WAV,<br/>detector contract tests"]
                WindowsAudioTests["TimeGrapher.Platform.WindowsAudio.Tests<br/>Windows audio backend tests"]
                LinuxAudioTests["TimeGrapher.Platform.LinuxAudio.Tests<br/>Linux audio backend tests"]

                AppTests ~~~ CoreTests
                CoreTests ~~~ WindowsAudioTests
                WindowsAudioTests ~~~ LinuxAudioTests
            end

            subgraph Support["supporting artifacts"]
                direction TB
                Docs["docs<br/>architecture, porting,<br/>review notes"]
                Deploy["deploy/linux<br/>Raspberry Pi desktop integration"]
                Ci[".github/workflows<br/>CI and release pipelines"]
                BuildConfig["root build config<br/>global.json, Directory.*.props,<br/>solution file"]
                Fixtures["TimeGrapherTestFilesWeishiMic<br/>manual WAV verification fixtures"]

                Docs ~~~ Deploy
                Deploy ~~~ Ci
                Ci ~~~ BuildConfig
                BuildConfig ~~~ Fixtures
            end

            Tests ~~~ Support
        end

        Src ~~~ QualityAndSupport
    end
```

## Module summary

| Module | Submodules / parts | Role |
|---|---|---|
| `TimeGrapher.App` | startup/settings files, `Views`, `ViewModels`, `Services`, `Tabs`, `Rendering`, `Audio`, `Assets` | Avalonia UI, run lifecycle coordination, tab frame routing/rendering, platform audio backend selection |
| `TimeGrapher.Core` | `Analysis`, `Detection`, `Detection/Scoring`, `Metrics`, `Imaging`, `AudioIo`, `Sim`, `Shared` | UI/OS-independent watch sound analysis engine and shared contracts. `Detection/Scoring` declares the veto-only `IBeatEventGate` socket (classical `PllMatchGate` now, ONNX TinyML gate later via a leaf inference project) plus the `BeatWindowFeatures` feature contract; `Detection` includes the default adaptive floor, regime guard, and PLL-guided post-lock min-peak sensitivity behavior; `Analysis` hosts the gate at the metrics choke point; `Sim` adds the ground-truth `DetectionScorer` |
| `TimeGrapher.Platform.WindowsAudio` | `AudioCaptureWorker`, `SystemAudioControl` | Windows live microphone capture and system-volume integration behind Core live-audio contracts |
| `TimeGrapher.Platform.LinuxAudio` | `LinuxLiveAudioWorker` | Linux live microphone capture through PipeWire/ALSA command-line tools behind Core live-audio contracts |
| `TimeGrapher.Verify` | console entry point | Headless generated/WAV verification tool that exercises the Core detection and metrics pipeline |
| `tests` | `TimeGrapher.App.Tests`, `TimeGrapher.Core.Tests`, `TimeGrapher.Platform.WindowsAudio.Tests`, `TimeGrapher.Platform.LinuxAudio.Tests` | Regression tests for UI support/services/rendering/tabs, Core analysis contracts, and Windows/Linux audio behavior |
| supporting artifacts | `docs`, `deploy/linux`, `.github/workflows`, root build config, `TimeGrapherTestFilesWeishiMic` | Architecture/course documentation, Raspberry Pi deployment integration, CI/release automation, shared build metadata, and manual WAV fixtures |
