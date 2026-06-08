# Layered View

이 문서는 TimeGrapherNet의 모듈을 논리적 layer로 묶고, 각 layer가 어떤 아래 layer를 사용할 수 있는지 보여준다. 화살표 `A --> B`는 `A` layer가 `B` layer를 사용할 수 있다는 뜻이며, 허용 사용 방향은 위에서 아래로만 흐른다.

이 프로젝트는 인접 layer만 사용할 수 있는 strict layering이 아니라, 위 layer가 필요한 아래 layer를 직접 사용할 수 있는 relaxed layering으로 정리한다. 단, 아래 layer가 위 layer를 사용하는 역방향 의존은 허용하지 않는다.

## Allowed-to-use layers

```mermaid
flowchart TB
    subgraph L1["Layer 1 - Entry points, presentation, validation"]
        direction TB
        App["TimeGrapher.App<br/>Avalonia UI, views, view models,<br/>services, rendering, tab routing"]
        Verify["TimeGrapher.Verify<br/>headless verification console"]
        TestSuites["Test projects<br/>App.Tests, Core.Tests,<br/>LinuxAudio.Tests"]
    end

    subgraph L2["Layer 2 - Platform adapter layer"]
        direction TB
        WindowsAudio["TimeGrapher.Platform.WindowsAudio<br/>Windows live-audio adapter"]
        LinuxAudio["TimeGrapher.Platform.LinuxAudio<br/>Linux live-audio adapter"]
    end

    subgraph L3["Layer 3 - Portable core services"]
        direction TB
        CoreEngine["Core engine modules<br/>Analysis, Detection, Metrics,<br/>Imaging, AudioIo, Sim"]
        CoreContracts["Core.Shared<br/>audio contracts, buffers,<br/>analysis frame DTOs"]
    end

    subgraph L4["Layer 4 - External technology services"]
        direction TB
        UiLibraries["Avalonia / ScottPlot<br/>desktop UI and plotting"]
        WindowsLibraries["NAudio<br/>Windows audio APIs"]
        LinuxServices["PipeWire / ALSA / DBus<br/>Linux audio services"]
        TestFramework["xUnit<br/>test framework"]
    end

    L1 -- "can use" --> L2
    L1 -- "can use" --> L3
    L1 -- "can use" --> L4
    L2 -- "can use" --> L3
    L2 -- "can use" --> L4
```

## Layer responsibilities

| Layer | Modules | Cohesive service offered |
|---|---|---|
| Layer 1 - Entry points, presentation, validation | `TimeGrapher.App`, `TimeGrapher.Verify`, `*.Tests` | User interaction, headless verification, and regression validation |
| Layer 2 - Platform adapter layer | `TimeGrapher.Platform.WindowsAudio`, `TimeGrapher.Platform.LinuxAudio` | OS-specific live-audio input behind Core audio contracts |
| Layer 3 - Portable core services | `TimeGrapher.Core` submodules | UI/OS-independent watch sound analysis, WAV I/O, simulation, metrics, and shared contracts |
| Layer 4 - External technology services | Avalonia, ScottPlot, NAudio, PipeWire/ALSA/DBus, xUnit | Framework, OS, plotting, audio, and testing capabilities supplied from outside the project |

## Allowed dependency rules

| Rule | Meaning in this project |
|---|---|
| Layer 1 can use Layer 2 | The UI can select Windows or Linux live-audio adapters; LinuxAudio tests can validate the Linux adapter |
| Layer 1 can use Layer 3 | The UI and verification console can call Core analysis, WAV, simulation, detection, and shared DTO modules |
| Layer 1 can use Layer 4 | UI code can use Avalonia/ScottPlot; tests can use xUnit |
| Layer 2 can use Layer 3 | Platform adapters implement Core live-audio contracts from `Core.Shared` |
| Layer 2 can use Layer 4 | Platform adapters can call OS/framework audio APIs such as NAudio, PipeWire, ALSA, and DBus |

## Constraints

| Constraint | Rationale |
|---|---|
| No lower-to-upper dependency | `TimeGrapher.Core` must not reference App or Platform projects, keeping analysis portable and testable |
| Platform-specific code is isolated | Windows and Linux audio implementations stay in platform adapter projects instead of being mixed into Core |
| UI technology stays above Core | Avalonia and ScottPlot are used by the app layer, not by the portable analysis engine |
| Test modules sit at the top | Tests can use runtime modules, but runtime modules must not depend on tests |
