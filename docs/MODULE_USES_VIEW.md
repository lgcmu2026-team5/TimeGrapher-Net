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
        WindowsAudioTests["TimeGrapher.Platform.WindowsAudio.Tests"]
        LinuxAudioTests["TimeGrapher.Platform.LinuxAudio.Tests"]
    end

    subgraph External["External libraries / OS services"]
        direction TB
        Avalonia["Avalonia"]
        ScottPlot["ScottPlot.Avalonia"]
        TmdsDbus["Tmds.DBus.Protocol"]
        NAudio["NAudio"]
        LinuxAudioStack["PipeWire / ALSA tools<br/>wpctl · pw-record · arecord"]
        Xunit["xUnit"]
    end

    App --> Core
    App -.-> WindowsAudio
    App -.-> LinuxAudio
    Verify --> Core
    WindowsAudio --> Core
    LinuxAudio --> Core

    AppTests --> App
    AppTests --> Core
    CoreTests --> Core
    WindowsAudioTests --> WindowsAudio
    WindowsAudioTests --> Core
    LinuxAudioTests --> LinuxAudio
    LinuxAudioTests --> Core

    App --> Avalonia
    App --> ScottPlot
    App --> TmdsDbus
    WindowsAudio --> NAudio
    LinuxAudio --> LinuxAudioStack
    AppTests --> Avalonia
    AppTests --> ScottPlot
    AppTests --> Xunit
    CoreTests --> Xunit
    WindowsAudioTests --> Xunit
    LinuxAudioTests --> Xunit
```

`TimeGrapher.App`의 플랫폼 오디오 `ProjectReference`는 `RuntimeIdentifier` 조건부다. RID가 없을 때는 개발/테스트 빌드를 위해 Windows와 Linux 어댑터가 모두 포함되고, `win-*` 또는 `linux-*` RID publish에서는 해당 플랫폼 어댑터만 포함된다.

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
        Tabs["Tabs<br/>catalog, registry, frame router"]
        Rendering["Rendering<br/>frame consumers, plots,<br/>readouts, images"]
        Assets["Assets<br/>icons, fonts, splash frames"]
    end

    subgraph UsedByApp["Used runtime modules"]
        direction TB
        CoreAnalysis["Core.Analysis"]
        CoreAudioIo["Core.AudioIo"]
        CoreDetection["Core.Detection"]
        CoreMetrics["Core.Metrics"]
        CoreShared["Core.Shared"]
        CoreSim["Core.Sim"]
        PlatformAudio["Platform audio backends"]
    end

    Program --> Views
    Program --> Audio
    Program --> Rendering
    Program --> CoreAnalysis
    Program --> CoreAudioIo
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
    ViewModels --> CoreShared
    Services --> ViewModels
    Services --> CoreAnalysis
    Services --> CoreAudioIo
    Services --> CoreMetrics
    Services --> CoreShared
    Audio --> CoreShared
    Audio --> PlatformAudio
    Tabs --> ViewModels
    Tabs --> Rendering
    Tabs --> CoreShared
    Rendering --> Tabs
    Rendering --> CoreAnalysis
    Rendering --> CoreMetrics
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
| `TimeGrapher.App` | `TimeGrapher.Core`, RID-selected platform audio backends, Avalonia, ScottPlot, Tmds.DBus.Protocol | UI is coupled to Core contracts/results, desktop UI libraries, and selected platform audio adapters |
| `TimeGrapher.Verify` | `TimeGrapher.Core` | Console verification shares the same analysis, detection, WAV, and simulator modules as the app |
| `TimeGrapher.Platform.WindowsAudio` | `TimeGrapher.Core.Shared`, NAudio | Windows input backend is coupled to Core live-audio contracts and NAudio APIs |
| `TimeGrapher.Platform.LinuxAudio` | `TimeGrapher.Core.Shared`, `wpctl`, `pw-record`, `arecord` | Linux input backend is coupled to Core live-audio contracts and Linux audio command-line tools |
| `TimeGrapher.App.Tabs` | `TimeGrapher.App.Rendering`, `TimeGrapher.App.ViewModels`, `TimeGrapher.Core.Shared` | Tab registration owns tab-to-consumer wiring and reads view-model state for position controls |
| `TimeGrapher.App.Rendering` | `TimeGrapher.App.Tabs`, `TimeGrapher.Core.Analysis`, `TimeGrapher.Core.Metrics`, `TimeGrapher.Core.Shared` | Frame consumers implement tab routing contracts and render Core frame/metric DTOs |
| `TimeGrapher.Core.Analysis` | `Detection`, `Metrics`, `Imaging`, `AudioIo`, `Shared` | Analysis coordinates the core algorithm modules and is the most coupled Core submodule |
| `*.Tests` | target runtime modules, Core DTO namespaces, App UI libraries where control tests construct them, xUnit | Tests depend on the modules they validate, direct contract DTOs used by assertions, and the xUnit test framework |
