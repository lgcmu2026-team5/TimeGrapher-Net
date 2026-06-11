# TimeGrapherNet

**English** · [한국어](README.ko.md)

A desktop app that listens to a mechanical watch's tick through a microphone and
analyzes, in real time, how accurate the watch is — plotting the result as graphs.
Rebuilt from the original Qt/C++ version in **Avalonia + C# (.NET 8)**, a single
codebase runs on both **Windows** and the **Raspberry Pi 5**.

[![Release](https://img.shields.io/github/v/release/lgcmu2026-team5/TimeGrapher-Net?style=for-the-badge&logo=github)](https://github.com/lgcmu2026-team5/TimeGrapher-Net/releases/latest)

![AvaloniaUI](https://img.shields.io/badge/AvaloniaUI-165BFF?style=for-the-badge&logo=avaloniaui&logoColor=white)
![.Net](https://img.shields.io/badge/.NET-5C2D91?style=for-the-badge&logo=.net&logoColor=white)
![C#](https://img.shields.io/badge/c%23-%23239120.svg?style=for-the-badge&logo=csharp&logoColor=white)
![Windows](https://img.shields.io/badge/Windows-0078D6?style=for-the-badge&logo=windows&logoColor=white)
![Raspberry Pi](https://img.shields.io/badge/-Raspberry_Pi-C51A4A?style=for-the-badge&logo=Raspberry-Pi)

## Quick Start

There are two ways to get started — **(A) download a prebuilt release** (run immediately, no dev tools), or **(B) build from source**.

### (A) Download a release (recommended — no build)

Download the self-contained bundle for your OS from the [Releases page](https://github.com/lgcmu2026-team5/TimeGrapher-Net/releases). No .NET SDK install required.

- **Windows** — extract `TimeGrapher-<tag>-win-x64.zip` and run `TimeGrapher.App.exe`.
- **Raspberry Pi 5** — extract `TimeGrapher-<tag>-linux-arm64.tar.gz` and run `./install.sh` (see the Raspberry Pi section below).

Each bundle ships a `.sha256` file so you can verify integrity (Windows: `Get-FileHash`; Pi: `sha256sum -c <file>.sha256`).

### (B) Build from source

Assumes a clean machine with nothing installed. Follow the steps for your OS.

#### Windows

1. **Install dependencies** — install the .NET SDK 8 and Git.

   ```powershell
   winget install Microsoft.DotNet.SDK.8
   winget install Git.Git
   ```

   Open a new terminal and confirm `dotnet --version` prints `8.0.x`.
   (No audio driver to install — the required NAudio is bundled in the build.)

2. **Clone + build**

   ```powershell
   git clone https://github.com/lgcmu2026-team5/TimeGrapher-Net.git
   cd TimeGrapher-Net
   dotnet build TimeGrapherNet.sln -c Release   # first build may take a few minutes for package restore
   ```

3. **Run**

   ```powershell
   dotnet run --project src/TimeGrapher.App                                   # launch the GUI
   dotnet run --project src/TimeGrapher.Verify -c Release -- --generated --byte-fixtures   # headless: detection accuracy only
   ```

#### Raspberry Pi 5 (ARM64)

Distributed as a self-contained package, so **the Pi needs no .NET install.**
Build on a dev PC (one prepared via the Windows steps above) and copy only the output to the Pi.

> 💡 Why a different CPU is fine: the build output isn't x64 machine code but a bundle of
> **CPU-neutral IL + the ARM64 .NET runtime**. The actual translation to ARM64 machine code
> happens on the Pi at run time.

1. **Install Pi dependencies** — on the Pi, install the libraries the GUI needs.

   ```bash
   sudo apt update
   sudo apt install -y libx11-6 libice6 libsm6 libfontconfig1 xwayland
   ```

   Mic input needs PipeWire or ALSA, which Pi OS usually includes by default.

   > ICU (`libicu`) is **intentionally omitted** above. The app is built in invariant
   > globalization mode (`InvariantGlobalization=true`, culture-neutral by design), so .NET
   > does not require system ICU.

2. **Build on the dev PC (produce the deployment package)**

   ```powershell
   dotnet publish src/TimeGrapher.App/TimeGrapher.App.csproj -c Release -r linux-arm64 --self-contained true -o publish
   ```

3. **Copy to the Pi and run**

   **(A) If you downloaded the release tarball — one `install.sh` does it (recommended):** extract
   and run it; it handles apt dependencies + the executable bit + icon/`.desktop` registration
   (paths are set automatically to the extract location).

   ```bash
   mkdir -p ~/timegrapher
   tar -xzf TimeGrapher-*-linux-arm64.tar.gz -C ~/timegrapher
   cd ~/timegrapher
   ./install.sh                 # apt deps + chmod + icon/.desktop registration (skip deps: --no-deps)
   ./TimeGrapher.App            # or 'TimeGrapher' from the menu/taskbar
   ./TimeGrapher.App --smoke    # headless self-check (device list: --audio-smoke)
   ```

   **(B) If it's a publish folder you built from source — run it manually:**

   ```bash
   # (dev PC) copy the publish folder to the Pi — e.g.:
   #   scp -r publish <user>@<pi-host>:~/timegrapher
   # (Pi) from the copied folder:
   cd ~/timegrapher
   chmod +x ./TimeGrapher.App
   ./TimeGrapher.App            # GUI when a monitor is attached
   ./TimeGrapher.App --smoke    # headless self-check (device list: --audio-smoke)
   ```

   For desktop-integration details such as manual taskbar-icon registration, see `deploy/linux/README.md`.

## Features

- Detects tick/tock to lock the beat (BPH) automatically or manually, and stays in sync via phase tracking.
- Analysis display tabs from the current app catalog: **Rate/Scope**, **Sound Print**, **Trace**, **Sweep**, **Vario**, **Beat Error**, **Filter Scope**, **Long-Term**, **Positions**, **Beat Noise**, **Escapement**, **Waveforms**, and **Spectrogram**.
- The **Positions** tab combines compact position-selection buttons on the left with per-position sequence measurements on the right.
- Three inputs: **Live** (mic), **Playback** (WAV file), **Sim** (synthetic signal).
- Records the input to WAV while analyzing.
- A console mode to check detection accuracy and audio devices headlessly.

## Why Avalonia / .NET

This project is a port of the original **Qt/C++** implementation to **Avalonia + C# (.NET 8)**.
The goal was "maintain and ship for both Windows and the Raspberry Pi together, from one codebase,
at low cost" — and in practice it paid off as follows.

### 1. Cross-building & releasing is nearly free

.NET uses a CPU-neutral **IL (intermediate language) + bundled runtime** model, so there is no
per-architecture recompile. `dotnet publish -r <RID>` isn't a recompile — it just bundles the
target's runtime pack with the same IL — so **two stock GitHub runners (Windows + Ubuntu) produce
all four targets** (nothing is executed, so no emulation is needed).

| Step | Avalonia / .NET (this project) | If it were Qt / C++ |
|---|---|---|
| Per-arch build | swap the `-r` value | a **cross-compile toolchain + sysroot** per target (aarch64-gcc, MSVC ARM64 …) |
| UI framework | NuGet resolves it per RID automatically | build/obtain **Qt per architecture** (ARM Linux is often built from source) |
| Native deps | automatic per-RID NuGet assets (e.g. SkiaSharp `.dll`/`.so`) | compile/obtain each library for the target ABI yourself (vcpkg·Conan) |
| Bundling | one `--self-contained` | gather libraries/plugins and fix rpath with `windeployqt`/`linuxdeployqt` |
| CI | four targets on two stock GitHub runners | per-target environment setup + usually **ARM runners or QEMU emulation** to run/test |

> This repo's multi-arch release (win-x64 · win-arm64 · linux-x64 · linux-arm64) took, in practice,
> **5 lines of csproj + a workflow matrix** — and zero lines of C# change.

### 2. Usability

**For users (end users)**
- **Self-contained distribution** — download and run, no .NET install. On Windows extract and run
  the `.exe`; on the Raspberry Pi, one `./install.sh`.
- **Four official binaries** — Windows x64/ARM and Linux x64/ARM64, served straight from Releases.
- **Consistent modern UI** — the Avalonia Fluent theme gives the same look across OSes, with
  real-time graphs via ScottPlot.

**For developers**
- **One solution, one language** — the only per-OS divergence is at the audio-backend boundary; everything else is shared.
- **Safety & productivity** — GC and nullable reference types cut out whole classes of manual-memory/pointer bugs and speed up iteration.
- **Simple tests & CI** — `Core` has no UI/OS dependencies, so unit testing is easy (currently 469
  tests) and runs on stock runners with no special setup.

## Architecture

The analysis engine (`Core`) has zero dependency on UI or OS. Only the per-OS audio backend is
swapped, and CI checks that boundary automatically.

```mermaid
graph TD
    App["App (Avalonia UI)"] --> Core["Core (analysis engine)"]
    Verify["Verify (validation console)"] --> Core
    WinAudio["WindowsAudio (NAudio)"] --> Core
    LinuxAudio["LinuxAudio (PipeWire/ALSA)"] --> Core
    App -. Windows build .-> WinAudio
    App -. Raspberry Pi build .-> LinuxAudio
```

*Figure 1. Every project references Core; only the audio backend matching the build-target OS is included.*

The flow from sound to screen:

```mermaid
flowchart LR
    A["Audio input<br/>mic · WAV · synth"] --> B["Buffer"]
    B --> C["Detect<br/>find tick/tock"]
    C --> D["Measure<br/>BPH · error · amplitude"]
    D --> E["Graph / image<br/>render"]
    E --> F["Display"]
```

*Figure 2. Input → detect → measure → visualize. One analysis pass drives one screen update.*

### Input worker contract

All three inputs (Live · Playback · Sim) implement the shared `IAudioInputWorker` (pause · stop ·
data-ready). Only mic input adds device selection, volume, and capture-end via `ILiveAudioWorker`.
Core only needs to know this small contract, so per-OS backends drop in freely.

```mermaid
classDiagram
    class IAudioInputWorker {
        <<interface>>
        +event DataReady
        +bool IsPaused
        +SetPaused(bool)
        +bool TryStop(TimeSpan)
    }
    class ILiveAudioWorker {
        <<interface>>
        +event CaptureEnded
        +Start(device, rate, volume)
        +SetVolume(float)
    }
    IAudioInputWorker <|-- ILiveAudioWorker
    ILiveAudioWorker <|.. AudioCaptureWorker : Windows
    ILiveAudioWorker <|.. LinuxLiveAudioWorker : Raspberry Pi
    IAudioInputWorker <|.. PlaybackWorker
    IAudioInputWorker <|.. SimWorker
```

*Figure 3. The input-worker hierarchy. Mic backends are implemented in per-OS assemblies.*

## Projects

| Project | Role |
|---|---|
| `TimeGrapher.Core` | Analysis engine — detection, measurement, image generation, WAV read/write, simulator (UI/OS-independent) |
| `TimeGrapher.App` | Avalonia UI — windows, tabs, graph display; wires up per-OS audio |
| `TimeGrapher.Platform.WindowsAudio` | Windows mic input (NAudio) |
| `TimeGrapher.Platform.LinuxAudio` | Raspberry Pi mic input (PipeWire → ALSA) |
| `TimeGrapher.Verify` | Headless console that checks BPH detection accuracy on WAV files |
| `*.Tests` | xUnit tests (Core / App / WindowsAudio / LinuxAudio) |

For deeper design background and the Qt→.NET porting story, see the `docs/` folder.

## Tech stack

| Package | Version | Purpose |
|---|---|---|
| Avalonia(.Desktop/.Themes.Fluent) | 11.3.17 | UI framework |
| ScottPlot.Avalonia | 5.0.55 | Scope/rate graphs |
| NAudio.Wasapi / NAudio.WinMM | 2.2.1 | Windows mic capture & volume |
| xunit / xunit.runner.visualstudio | 2.9.2 / 2.8.2 | Testing |
| Microsoft.NET.Test.Sdk | 17.12.0 | Test host |

Package versions are managed centrally in `Directory.Packages.props` and pinned with
`packages.lock.json`, so restores are always reproducible.

## Tests / CI

**All 469 tests pass** under `dotnet test` (App 236 / Core 217 / WindowsAudio 6 / LinuxAudio 10).

```mermaid
pie showData
    title Test distribution (469 total)
    "App.Tests" : 236
    "Core.Tests" : 217
    "Platform.WindowsAudio.Tests" : 6
    "Platform.LinuxAudio.Tests" : 10
```

*Figure 4. Test distribution.*

GitHub Actions (`.github/workflows/ci.yml`) runs the following on every push/PR across Ubuntu and
Windows — build & test, WAV detection verification, and Raspberry Pi/Windows deployment-artifact generation.

Releases are handled by a separate workflow (`.github/workflows/release.yml`). Pushing a `v*` tag
(or a manual dispatch) builds the win-x64 · win-arm64 · linux-x64 · linux-arm64 self-contained
bundles (`.zip`/`.tar.gz` + `.sha256`) and publishes them as a GitHub Release (release notes
auto-generated; tags containing `-`, e.g. `v0.1.0-rc.1`, are marked prerelease). To cut a release:

```bash
git tag v0.1.0 && git push origin v0.1.0
```

## Checklist

| Item | Command | Status |
|---|---|---|
| Build | `dotnet build TimeGrapherNet.sln -c Release` | ✅ |
| Test | `dotnet test TimeGrapherNet.sln -c Release` (469/469) | ✅ |
| Detection check | `... TimeGrapher.Verify -- --generated --byte-fixtures` (exit 0) | ✅ |
| GUI run | `dotnet run --project src/TimeGrapher.App` | ✅ |
| Deploy — Raspberry Pi (linux-arm64) | `dotnet publish ... -r linux-arm64 --self-contained true` | ✅ |
| Deploy — Linux x64 | `dotnet publish ... -r linux-x64 --self-contained true` | ✅ |
| Deploy — Windows ARM (win-arm64) | `dotnet publish ... -r win-arm64 --self-contained true` | ✅ |
| Raspberry Pi mic input | verify with a capture device attached | ⏳ awaiting device |
