# Linux (Raspberry Pi 5) 설치 · 실행 · 데스크톱 통합

릴리즈의 `TimeGrapher-<버전>-linux-arm64.tar.gz`에는 이 README, 앱 바이너리,
아이콘(`AppIcon-256.png`), 데스크톱 엔트리(`TimeGrapher.App.desktop`), 설치
스크립트(`install.sh`)가 함께 들어 있다.

## 1. 빠른 설치 (권장)

압축을 풀고 `install.sh`를 한 번 실행하면 의존성 설치 + 실행 권한 + 아이콘/데스크톱
엔트리 등록까지 끝난다. 데스크톱 엔트리의 `Exec`/`Icon` 경로는 **푼 위치로 자동 설정**된다.

```bash
mkdir -p ~/timegrapher
tar -xzf TimeGrapher-*-linux-arm64.tar.gz -C ~/timegrapher
cd ~/timegrapher
./install.sh           # apt 의존성 + chmod + 아이콘/.desktop 설치 (의존성 생략: --no-deps)
./TimeGrapher.App      # 또는 메뉴/작업표시줄의 'TimeGrapher'
```

- `install.sh`는 멱등(재실행 가능). 의존성은 `apt-get`이 있을 때만 설치하고, root가
  아니면 `sudo`를 쓴다.
- 헤드리스/SSH 점검: `./TimeGrapher.App --smoke` (GUI 없이 자가점검, 성공 시 종료코드 0).

self-contained 빌드라 **.NET 런타임은 설치할 필요 없다.** 아래 2·3번은 `install.sh`가
자동으로 하는 일을 수동으로 하거나 문제를 진단할 때 참고하는 내용이다.

## 2. 런타임 의존성 (수동 — 신선한 Pi OS에서 한 번만)

self-contained 번들은 .NET 런타임은 포함하지만 시스템 X11/폰트 라이브러리는 포함하지
않는다. 창이 안 뜨거나 폰트가 안 보이면 설치한다:

```bash
sudo apt update
sudo apt install -y libx11-6 libice6 libsm6 libfontconfig1 xwayland
```

- `libx11-6 libice6 libsm6` — Avalonia X11 백엔드. 없으면 윈도우 백엔드 초기화 실패.
- `libfontconfig1` — 폰트. 없으면 텍스트 렌더링/시작 실패.
- `xwayland` — Pi OS는 Wayland 세션이 기본인데 Avalonia는 X11 백엔드라 XWayland로 동작.
  창이 안 뜨면 이걸 먼저 의심할 것.
- (선택) 직접 DRM/KMS 풀스크린을 쓰면 `libgbm1 libdrm2 libinput10`도 필요.

> ICU(`libicu`)는 **필요 없다.** 앱이 invariant globalization 모드로 빌드되어 있어
> (`InvariantGlobalization=true`) .NET이 시스템 ICU를 요구하지 않는다.

> 64비트 유저랜드 필수: `dpkg --print-architecture`가 `arm64`여야 한다
> (armhf면 이 arm64 빌드는 실행되지 않는다).

헤드리스/SSH에서는 GUI 대신 CLI 스모크 플래그로 점검한다:

```bash
./TimeGrapher.App --smoke   # 헤드리스 자가 점검, 성공 시 종료코드 0
```

## 3. 데스크톱 통합 (수동 — 작업표시줄 아이콘)

> `install.sh`가 이 과정을 자동으로 처리한다(경로 자동 설정 포함). 아래는 수동 설치나
> 동작 원리 참고용이다.

Pi OS의 작업표시줄(wf-panel-pi, Wayland)은 앱 윈도우가 제공하는 아이콘(`_NET_WM_ICON`)을
사용하지 않고, 윈도우의 app-id(XWayland에서는 `WM_CLASS` = 엔트리 어셈블리명
`TimeGrapher.App`)와 매칭되는 `.desktop` 파일의 `Icon=`만 사용한다. 따라서 작업표시줄
아이콘은 아래 두 파일을 Pi에 설치해야 표시된다.

```bash
# 1) 아이콘 (tarball에 동봉된 AppIcon-256.png — 저장소 경로는 src/TimeGrapher.App/Assets/App/AppIcon-256.png)
mkdir -p ~/.local/share/icons
cp AppIcon-256.png ~/.local/share/icons/timegrapher.png

# 2) 데스크톱 엔트리 (Exec/Icon 경로는 배포 위치에 맞게 수정)
mkdir -p ~/.local/share/applications
cp TimeGrapher.App.desktop ~/.local/share/applications/
```

- `Exec`/`Icon` 경로는 기본값이 팀 Pi(`team5`) 기준이므로 다른 환경에서는 수정할 것.
  (예: 위에서 `~/timegrapher`에 풀었다면 `Exec=/home/<user>/timegrapher/TimeGrapher.App`)
- 파일명은 app-id와 같은 `TimeGrapher.App.desktop`을 유지해야 패널이 매칭한다
  (`StartupWMClass`도 함께 지정되어 있음).
- 적용이 안 보이면 앱 재시작, 그래도 안 되면 `pkill wf-panel-pi`(패널 자동 재실행).
