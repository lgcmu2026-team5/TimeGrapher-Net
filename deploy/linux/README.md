# Linux (Raspberry Pi) 데스크톱 통합

Pi OS의 작업표시줄(wf-panel-pi, Wayland)은 앱 윈도우가 제공하는 아이콘(`_NET_WM_ICON`)을
사용하지 않고, 윈도우의 app-id(XWayland에서는 `WM_CLASS` = 엔트리 어셈블리명
`TimeGrapher.App`)와 매칭되는 `.desktop` 파일의 `Icon=`만 사용한다. 따라서 작업표시줄
아이콘은 아래 두 파일을 Pi에 설치해야 표시된다.

```bash
# 1) 아이콘 (저장소의 src/TimeGrapher.App/Assets/App/AppIcon-256.png 를 복사)
mkdir -p ~/.local/share/icons
cp AppIcon-256.png ~/.local/share/icons/timegrapher.png

# 2) 데스크톱 엔트리 (Exec/Icon 경로는 배포 위치에 맞게 수정)
mkdir -p ~/.local/share/applications
cp TimeGrapher.App.desktop ~/.local/share/applications/
```

- `Exec`/`Icon` 경로는 기본값이 팀 Pi(`team5`) 기준이므로 다른 환경에서는 수정할 것.
- 파일명은 app-id와 같은 `TimeGrapher.App.desktop`을 유지해야 패널이 매칭한다
  (`StartupWMClass`도 함께 지정되어 있음).
- 적용이 안 보이면 앱 재시작, 그래도 안 되면 `pkill wf-panel-pi`(패널 자동 재실행).
