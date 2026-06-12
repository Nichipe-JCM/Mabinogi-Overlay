# Mabinogi Overlay

Mabinogi Overlay는 마비노기 퀵슬롯 중 사용자가 선택한 칸을 별도 오버레이로 복제해서 보여주는 Windows 데스크톱 유틸리티입니다. 목적은 쿨타임 시인성을 높이는 것이며, 게임 메모리 읽기, 코드 인젝션, 렌더링 훅, 입력 자동화는 하지 않습니다.

현재 버전: `0.0.1`

## 현재 상태

이 버전은 초기 portable Windows 빌드입니다. 기본 사용 흐름은 구현되어 있지만, 실제 마비노기 UI 배치, 해상도, DPI 배율, 퀵슬롯 구성에 따른 감지 정확도와 편집 편의성은 계속 조정 중입니다.

## 주요 기능

- 실행 중인 마비노기 `Client.exe` 창 선택
- Windows Graphics Capture 검증 및 사용
- 게임 창을 Capture Preview로 캡쳐
- 사용자가 드래그한 ROI 기준으로 퀵슬롯 섹션 자동 감지
- 상단 가로형 퀵슬롯과 세로형 퀵슬롯 지원
- 슬롯 후보 선택, 다중선택, 이동, 크기 조정, 추가, 삭제
- 기존 오버레이 레이아웃을 유지한 채 선택 후보만 추가
- Overlay Layout Editor에서 캔버스 크기, 위치, 슬롯별 투명도, 슬롯별 스케일, grid snap, max FPS, stop hotkey 편집
- 실제 화면 위에 Screen Preview를 띄워 위치 조정
- 게임 클릭을 방해하지 않는 topmost click-through 오버레이 실행
- 모든 slot candidates, section, overlay slot, layout 설정을 JSON 프로필로 저장/로드

## 기술 스택

### 런타임과 UI

- **언어:** C#
- **런타임:** .NET 8
- **대상 프레임워크:** `net8.0-windows10.0.19041.0`
- **UI 프레임워크:** WPF
- **플랫폼:** Windows 전용

WPF를 선택한 이유는 첫 버전에서 가장 중요한 부분이 크로스플랫폼 UI가 아니라 Windows 캡쳐, 오버레이, 보정 편집 UI이기 때문입니다. 이 앱은 Windows API 의존도가 높으므로 Windows 네이티브에 가까운 C# + WPF 구성이 구현과 검증 모두에 유리합니다.

### 캡쳐

- **주 캡쳐 API:** Windows Graphics Capture
- **Interop:** WinRT `Windows.Graphics.Capture`
- **그래픽 브릿지:** Direct3D 11 interop
- **프레임 처리:** `Direct3D11CaptureFramePool` 기반 지속 live capture session
- **정지 화면 캡쳐:** Preview와 보정을 위한 one-shot WGC capture

실제 오버레이 갱신에서는 WGC 세션을 계속 유지하고 `FrameArrived`로 들어온 최신 프레임을 처리합니다. 매 갱신마다 WGC 세션을 새로 만들고 폐기하는 방식보다 성능과 안정성 면에서 유리합니다.

### 오버레이 창

- **창 구현:** WPF overlay window
- **Native 동작:** Win32 extended window styles
- **Click-through 플래그:** `WS_EX_TRANSPARENT`, `WS_EX_LAYERED`, `WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW`
- **항상 위 동작:** Win32 topmost positioning
- **중지 단축키:** Win32 global hotkey

오버레이는 게임 위에 보이되, 마우스 클릭은 게임으로 통과시키는 것을 목표로 합니다.

### 감지와 보정

- **감지 방식:** ROI 기반 quickslot section detection
- **Anchor 전략:** 우하단 테두리 기준 anchor scan
- **점수 계산:** anchor 후보 생성 후 전체 section pattern score 평가
- **지원 패턴:**
  - `Top grouped 4x2 x3`
  - `Vertical 2x8`
- **수동 보정:** 후보 드래그, 박스 선택, Ctrl/Shift 다중선택, 방향키 미세조정, undo/redo

Detect는 완전 자동화를 목표로 하지 않고, 사용자의 보정을 돕는 기능입니다. 감지된 모든 슬롯 후보는 사용자가 직접 수정한 뒤 overlay에 추가할 수 있습니다.

## 프로필 저장

- **형식:** JSON
- **Serializer:** `System.Text.Json`
- **Portable 기본 경로:** 실행 파일 옆 `save` 폴더
- **설정 파일:** 실행 파일 옆 `settings.json`

프로필에 저장되는 항목은 다음과 같습니다.

- 모든 slot candidates
- quickslot sections
- overlay slots
- canvas size
- screen position
- opacity
- slot scale
- max FPS
- stop hotkey
- grid snap size
- 수동 섹션 생성 설정

## 저장소 구조

```text
src/
  TestOverlay.App/
    MainWindow.xaml(.cs)                  메인 보정 및 제어 UI
    LayoutEditorWindow.xaml(.cs)          오버레이 레이아웃 편집기
    OverlayWindow.xaml(.cs)               실제 click-through 오버레이
    OverlayPlacementPreviewWindow.xaml(.cs)
    Models/                               앱 상태와 프로필 모델
    Native/                               Win32 및 Direct3D interop
    Services/                             캡쳐, 감지, 프로필, 핫키 서비스
tools/
  TestOverlay.DetectionProbe/             감지 테스트/분석용 도구
docs/
  PLANNING.md
  MVP_MANUAL_TEST.md
  NEXT_SESSION_HANDOFF.md
```

## 빌드

필요 조건:

- Windows 10 2004 이상
- .NET 8 SDK

빌드 명령:

```powershell
dotnet build G:\gpt\git\testoverlayproj\src\TestOverlay.App\TestOverlay.App.csproj
```

## Portable Publish

권장 publish 명령:

```powershell
dotnet publish G:\gpt\git\testoverlayproj\src\TestOverlay.App\TestOverlay.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  /p:PublishSingleFile=true `
  /p:PublishReadyToRun=true `
  /p:PublishTrimmed=false `
  -o G:\gpt\git\testoverlayproj\artifacts\MabinogiOverlay-0.0.1
```

권장 패키지 구조:

```text
MabinogiOverlay-0.0.1/
  Mabinogi Overlay.exe
  README.md
  save/
```

Portable 빌드는 쓰기 가능한 폴더에서 실행하는 것을 권장합니다. `Program Files`처럼 쓰기 권한이 제한되는 위치에서는 `settings.json`과 `save` 폴더 생성이 막힐 수 있습니다.

## 패키징 전 수동 테스트

공개 빌드 전 다음 항목을 확인합니다.

- Window list에서 `Client.exe`가 우선 선택되는지 확인
- `Verify WGC` 실행
- `Capture` 후 Preview에 게임 화면이 정확히 표시되는지 확인
- 필요한 각 퀵슬롯 섹션을 Auto detect
- 필요한 슬롯을 Overlay에 추가
- Layout Editor에서 오버레이 위치와 슬롯 배치 조정
- Screen Preview로 실제 게임 화면에 맞게 위치 조정
- `Start`로 오버레이 실행
- 오버레이가 게임 위에 계속 보이는지 확인
- 오버레이가 포커스를 빼앗거나 마우스 클릭을 먹지 않는지 확인
- Stop 버튼과 stop hotkey 동작 확인
- Portable `save` 폴더 기준으로 프로필 저장/로드 확인

## 안전 경계

이 프로젝트는 다음을 의도적으로 하지 않습니다.

- 게임 메모리 읽기
- 프로세스 인젝션
- DLL 인젝션
- 게임 DirectX/OpenGL 렌더링 훅
- 게임 입력 자동화
- 안티치트 우회 또는 은닉 동작

공식 Windows 캡쳐 API와 별도 투명 오버레이 창만 사용합니다.
