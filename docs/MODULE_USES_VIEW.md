# Module Uses View

이 문서는 TimeGrapherNet의 모듈 사용 관계를 보여준다. 화살표 `A --> B`는 `A` 모듈이 `B` 모듈을 사용한다는 뜻이며, 이 관계가 모듈 간 결합을 만든다.

## Project-level uses

```mermaid
flowchart TB
    subgraph Runtime["Runtime modules"]
        direction TB
        App["TimeGrapher.App<br/>Avalonia UI"]
        Verify["TimeGrapher.Verify<br/>headless verification"]
        WindowsAudio["TimeGrapher.Platform.WindowsAudio<br/>Windows live audio"]
        LinuxAudio["TimeGrapher.Platform.LinuxAudio<br/>Linux live audio"]
        Core["TimeGrapher.Core<br/>analysis engine and contracts"]
    end

    subgraph Tests["Test modules"]
        direction TB
        AppTests["TimeGrapher.App.Tests"]
        CoreTests["TimeGrapher.Core.Tests"]
        LinuxAudioTests["TimeGrapher.Platform.LinuxAudio.Tests"]
    end

    subgraph External["External libraries / OS services"]
        direction TB
        Avalonia["Avalonia"]
        ScottPlot["ScottPlot.Avalonia"]
        NAudio["NAudio"]
        LinuxAudioStack["PipeWire / ALSA"]
        Xunit["xUnit"]
    end

    App --> Core
    App --> WindowsAudio
    App --> LinuxAudio
    Verify --> Core
    WindowsAudio --> Core
    LinuxAudio --> Core

    AppTests --> App
    CoreTests --> Core
    LinuxAudioTests --> LinuxAudio

    App --> Avalonia
    App --> ScottPlot
    WindowsAudio --> NAudio
    LinuxAudio --> LinuxAudioStack
    AppTests --> Xunit
    CoreTests --> Xunit
    LinuxAudioTests --> Xunit
```

## App internal uses

```mermaid
flowchart TB
    subgraph App["TimeGrapher.App"]
        direction TB
        Program["Program / App startup"]
        Views["Views<br/>MainWindow, SplashWindow"]
        ViewModels["ViewModels<br/>UI state, commands"]
        Services["Services<br/>run, selection, recording, dialogs"]
        Audio["Audio<br/>backend selection, smoke check"]
        Tabs["Tabs<br/>tab catalog, routing"]
        Rendering["Rendering<br/>scope/rate, sound print"]
        Assets["Assets<br/>icons, fonts, splash frames"]
    end

    subgraph UsedByApp["Used runtime modules"]
        direction TB
        CoreAnalysis["Core.Analysis"]
        CoreAudioIo["Core.AudioIo"]
        CoreDetection["Core.Detection"]
        CoreShared["Core.Shared"]
        CoreSim["Core.Sim"]
        PlatformAudio["Platform audio backends"]
    end

    Program --> Audio
    Program --> CoreShared
    Views --> ViewModels
    Views --> Services
    Views --> Audio
    Views --> Tabs
    Views --> Rendering
    Views --> CoreAnalysis
    Views --> CoreAudioIo
    Views --> CoreDetection
    Views --> CoreShared
    Views --> CoreSim
    Services --> ViewModels
    Services --> CoreAnalysis
    Services --> CoreAudioIo
    Services --> CoreShared
    Audio --> CoreShared
    Audio --> PlatformAudio
    Tabs --> Rendering
    Tabs --> CoreShared
    Rendering --> Tabs
    Rendering --> CoreShared
    Views --> Assets
```

## Core internal uses

```mermaid
flowchart TB
    subgraph Core["TimeGrapher.Core"]
        direction TB
        Analysis["Analysis<br/>workers, detector metrics,<br/>frame projectors"]
        Detection["Detection<br/>tick/tock detection,<br/>BPH, sync"]
        Metrics["Metrics<br/>watch metrics,<br/>rolling statistics"]
        Imaging["Imaging<br/>sound image renderer"]
        AudioIo["AudioIo<br/>WAV I/O,<br/>playback worker"]
        Sim["Sim<br/>synthetic input worker"]
        Shared["Shared<br/>contracts, buffers,<br/>frame DTOs"]
    end

    Analysis --> Detection
    Analysis --> Metrics
    Analysis --> Imaging
    Analysis --> AudioIo
    Analysis --> Shared
    AudioIo --> Shared
    Metrics --> Shared
    Imaging --> Shared
    Sim --> Shared
```

## Coupling summary

| Using module | Used module(s) | Coupling created |
|---|---|---|
| `TimeGrapher.App` | `TimeGrapher.Core`, platform audio backends, Avalonia, ScottPlot | UI is coupled to Core contracts/results and to selected platform audio adapters |
| `TimeGrapher.Verify` | `TimeGrapher.Core` | Console verification shares the same analysis, detection, WAV, and simulator modules as the app |
| `TimeGrapher.Platform.WindowsAudio` | `TimeGrapher.Core.Shared`, NAudio | Windows input backend is coupled to Core live-audio contracts and NAudio APIs |
| `TimeGrapher.Platform.LinuxAudio` | `TimeGrapher.Core.Shared`, PipeWire/ALSA environment | Linux input backend is coupled to Core live-audio contracts and Linux audio facilities |
| `TimeGrapher.Core.Analysis` | `Detection`, `Metrics`, `Imaging`, `AudioIo`, `Shared` | Analysis coordinates the core algorithm modules and is the most coupled Core submodule |
| `*.Tests` | target runtime modules, xUnit | Tests depend on the modules they validate and on the xUnit test framework |
