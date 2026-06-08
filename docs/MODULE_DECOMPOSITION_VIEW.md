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
                    AppViews["Views<br/>MainWindow, SplashWindow"]
                    AppViewModels["ViewModels<br/>state, commands"]
                    AppServices["Services<br/>run lifecycle, selection,<br/>recording, dialogs"]
                    AppTabs["Tabs<br/>catalog, registry,<br/>frame routing"]
                    AppRendering["Rendering<br/>scope/rate,<br/>sound-print"]
                    AppAudio["Audio<br/>backend selection,<br/>smoke check"]
                    AppAssets["Assets<br/>icons, fonts,<br/>splash frames"]

                    AppViews ~~~ AppViewModels
                    AppViewModels ~~~ AppServices
                    AppServices ~~~ AppTabs
                    AppTabs ~~~ AppRendering
                    AppRendering ~~~ AppAudio
                    AppAudio ~~~ AppAssets
                end

                subgraph Core["TimeGrapher.Core<br/>analysis engine"]
                    direction TB
                    CoreAnalysis["Analysis<br/>worker, metrics,<br/>frame projectors"]
                    CoreDetection["Detection<br/>tick/tock, BPH,<br/>sync"]
                    CoreMetrics["Metrics<br/>watch metrics,<br/>rolling statistics"]
                    CoreImaging["Imaging<br/>sound image renderer"]
                    CoreAudioIo["AudioIo<br/>WAV I/O,<br/>playback worker"]
                    CoreSim["Sim<br/>synthetic watch signal"]
                    CoreShared["Shared<br/>contracts, buffers,<br/>frame DTOs"]

                    CoreAnalysis ~~~ CoreDetection
                    CoreDetection ~~~ CoreMetrics
                    CoreMetrics ~~~ CoreImaging
                    CoreImaging ~~~ CoreAudioIo
                    CoreAudioIo ~~~ CoreSim
                    CoreSim ~~~ CoreShared
                end

                App ~~~ Core
            end

            subgraph RuntimeSupport["platform and verification"]
                direction LR

                subgraph Platform["TimeGrapher.Platform<br/>OS audio backends"]
                    direction TB
                    WindowsAudio["WindowsAudio<br/>NAudio live input"]
                    LinuxAudio["LinuxAudio<br/>PipeWire/ALSA live input"]

                    WindowsAudio ~~~ LinuxAudio
                end

                Verify["TimeGrapher.Verify<br/>headless verification console"]

                Platform ~~~ Verify
            end
        end

        subgraph QualityAndSupport["tests and supporting artifacts"]
            direction LR

            subgraph Tests["tests"]
                direction TB
                AppTests["TimeGrapher.App.Tests<br/>UI support, services,<br/>rendering tests"]
                CoreTests["TimeGrapher.Core.Tests<br/>analysis, WAV,<br/>detector contract tests"]
                LinuxAudioTests["TimeGrapher.Platform.LinuxAudio.Tests<br/>Linux audio backend tests"]

                AppTests ~~~ CoreTests
                CoreTests ~~~ LinuxAudioTests
            end

            subgraph Support["supporting artifacts"]
                direction TB
                Docs["docs<br/>architecture, porting,<br/>review notes"]
                Deploy["deploy/linux<br/>Raspberry Pi desktop integration"]

                Docs ~~~ Deploy
            end

            Tests ~~~ Support
        end

        Src ~~~ QualityAndSupport
    end
```

## Module summary

| Module | Submodules / parts | Role |
|---|---|---|
| `TimeGrapher.App` | `Views`, `ViewModels`, `Services`, `Tabs`, `Rendering`, `Audio`, `Assets` | Avalonia UI, run lifecycle coordination, graph/sound-print rendering, platform audio backend selection |
| `TimeGrapher.Core` | `Analysis`, `Detection`, `Metrics`, `Imaging`, `AudioIo`, `Sim`, `Shared` | UI/OS-independent watch sound analysis engine and shared contracts |
| `TimeGrapher.Platform` | `TimeGrapher.Platform.WindowsAudio`, `TimeGrapher.Platform.LinuxAudio` | OS-specific live microphone input implementations behind Core live-audio contracts |
| `TimeGrapher.Verify` | console entry point | Headless WAV/generated-signal verification tool |
| `tests` | `TimeGrapher.App.Tests`, `TimeGrapher.Core.Tests`, `TimeGrapher.Platform.LinuxAudio.Tests` | Regression tests for UI support services, analysis contracts, and Linux audio behavior |
