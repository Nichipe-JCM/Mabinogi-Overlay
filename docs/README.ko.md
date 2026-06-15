# Mabinogi Overlay

Mabinogi Overlay는 마비노기 퀵슬롯 중 사용자가 선택한 칸을 별도 오버레이로 복제해서 보여주는 portable Windows 데스크톱 유틸리티입니다. 목적은 쿨타임 시인성을 높이는 것이며, 게임 메모리 읽기, 코드 인젝션, 렌더링 훅, 입력 자동화는 하지 않습니다.

현재 버전: `0.0.2-beta`

## 현재 상태

이 버전은 초기 beta 빌드입니다. 캡쳐, 디텍트, 레이아웃 편집, 프로필, 다국어 UI, 오버레이 실행 흐름은 구현되어 있지만, 공개 배포 전에는 실제 마비노기 클라이언트, 해상도, DPI, 퀵슬롯 구성, 캡쳐 방식에 대해 수동 검증이 필요합니다.

## 0.0.1 이후 주요 변경점

- WGC window capture, DXGI desktop duplication, GDI BitBlt 캡쳐 방식 선택 추가
- Preview capture를 Auto capture와 Manual capture로 분리
- GPU/DXGI live overlay renderer와 개선 CPU composited renderer 추가
- 30초 부하 테스트와 GPU 케이스를 포함한 renderer benchmark 기능 추가
- WGC live capture 처리와 런타임 진단 로그 개선
- 오버레이 실행 중 Layout Editor를 열 수 없도록 차단
- 어두운 맵 ROI 인식, 1px 테두리 처리, 과대 슬롯 정규화, 어두운 빈 영역 제외 로직 개선
- 프로그램 세션 단위 detect diagnostics 로그 추가
- WPF UI 기반 관리 화면 리디자인, 커스텀 상단바, 앱 로고/아이콘, 캡쳐 대기 화면, Settings 레이아웃 개선
- 한국어 UI와 언어 변경 즉시 갱신 지원
- Profile, renderer, capture backend, language, log, benchmark 설정을 Settings로 이동
- Slot Candidates 패널, manual section 팝업, 선택 스타일, Ctrl/Shift 다중선택 개선
- Auto detect section에서 ROI 모양으로 자동 라우팅: 가로로 긴 ROI는 상단 가로형, 세로로 긴 ROI는 세로형
- Capture Preview에서 드래그/선택 중 우클릭 취소 추가
- 디텍트 활성 상태와 드래그 선택 영역 색상을 프로젝트 강조색 `#89DED4`로 통일
- 전역 Slot scale이 `0.5x` 아래에서도 실제 오버레이 슬롯 크기에 반영되도록 수정

## 주요 기능

- 실행 중인 마비노기 `Client.exe` 창 우선 탐색 및 선택
- 게임 창을 Capture Preview로 캡쳐
- 감지된 마비노기 클라이언트를 바로 캡쳐하는 Auto capture
- WGC 선택 창을 통해 직접 캡쳐하는 Manual capture
- 사용자가 드래그한 ROI 기준으로 퀵슬롯 섹션 자동 감지
- 상단 가로형 퀵슬롯과 세로형 퀵슬롯 지원
- 슬롯 후보 선택, 다중선택, 이동, 크기 조정, 추가, 삭제
- 기존 오버레이 레이아웃을 유지한 채 선택 후보만 추가
- 감지가 틀렸을 때 사용할 수 있는 manual candidate와 manual section 도구
- Overlay Layout Editor에서 캔버스 크기, 화면 위치, 전역 투명도, 전역 슬롯 배율, grid snap, max FPS, stop hotkey, 슬롯별 투명도, 슬롯별 배율 편집
- 실제 화면 위에 Screen Preview를 띄워 위치 조정
- 게임 클릭을 방해하지 않는 topmost click-through 오버레이 실행
- 모든 slot candidates, section, overlay slot, layout 설정을 JSON 프로필로 저장/로드
- 영어/한국어 UI 전환
- Settings에서 현재 세션 로그 확인

## 기술 스택

### 런타임과 UI

- **언어:** C#
- **런타임:** .NET 8
- **대상 프레임워크:** `net8.0-windows10.0.19041.0`
- **UI 프레임워크:** WPF
- **UI 라이브러리:** WPF-UI
- **플랫폼:** Windows 전용

이 앱은 캡쳐, 오버레이, 클릭 통과, 전역 단축키, DirectX interop이 모두 Windows API에 의존합니다. 그래서 크로스플랫폼 UI보다 Windows 동작을 직접 검증하기 좋은 WPF 기반으로 구성되어 있습니다.

### 캡쳐

- **WGC window capture:** 마비노기 창만 정확히 캡쳐하기 위한 기본 캡쳐 방식
- **DXGI desktop duplication:** WGC 노란 테두리를 피하고 싶은 경우 비교 가능한 모니터 캡쳐 방식
- **GDI BitBlt:** client area 캡쳐 fallback
- **Interop:** WinRT `Windows.Graphics.Capture`, Direct3D 11, DXGI, Direct2D, DirectComposition

Preview capture는 선택한 마비노기 창을 직접 잡을 수 있는 WGC가 가장 적합합니다. Live overlay는 사용자 환경에 따라 DXGI/GDI도 비교할 수 있게 되어 있습니다.

### 오버레이 렌더러

지원 renderer mode:

- **Existing CPU/WPF:** 기존 WPF 기반 오버레이 갱신 경로
- **Improved CPU/Composited:** GPU 의존을 줄이기 위한 개선 CPU crop/update 경로
- **GPU/DXGI:** DXGI/DirectComposition 기반 GPU 지향 live overlay 경로. 선택한 capture path와 맞지 않으면 CPU fallback 사용

기본 renderer 설정은 GPU/DXGI입니다. 런타임 로그에는 renderer fallback과 성능 진단 정보가 기록됩니다.

### 오버레이 창

- **창 구현:** WPF overlay window와 Win32 native style 조합
- **Click-through 플래그:** `WS_EX_TRANSPARENT`, `WS_EX_LAYERED`, `WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW`
- **항상 위 동작:** Win32 topmost positioning
- **중지 단축키:** Win32 global hotkey

오버레이는 게임 위에 보이되, 마우스 클릭은 게임으로 통과시키는 것을 목표로 합니다.

### 감지와 보정

- **감지 방식:** ROI 기반 quickslot section detection
- **라우팅:** ROI 종횡비로 section pattern 자동 선택
- **Anchor 전략:** 우하단 테두리 기준 scanning
- **점수 계산:** anchor 후보 생성 후 전체 section pattern score 평가
- **지원 패턴:**
  - `Top grouped 4x2 x3`
  - `Vertical 2x8`
- **어두운 맵 보정:** 1px 테두리 우선, 과대 fit 정규화, adaptive inset content check, 어두운 빈 영역 제외
- **수동 보정:** 후보 드래그, 박스 선택, Ctrl/Shift 다중선택, 방향키 미세조정, grid snap, undo/redo

Detect는 완전 자동화를 목표로 하지 않고, 사용자의 보정을 돕는 기능입니다. 감지된 모든 슬롯 후보는 사용자가 직접 수정한 뒤 overlay에 추가할 수 있습니다.

## 프로필과 설정 저장

- **프로필 형식:** `System.Text.Json` 기반 JSON
- **Portable 기본 프로필 경로:** 실행 파일 옆 `save` 폴더
- **설정 파일:** 실행 파일 옆 `settings.json`
- **프로필 저장 경로:** Settings에서 변경 가능

프로필에 저장되는 항목:

- 모든 slot candidates
- quickslot sections
- overlay slots
- canvas size
- screen position
- global opacity
- global slot scale
- per-slot opacity override
- per-slot scale
- max FPS
- stop hotkey
- grid snap size
- manual section settings

## 저장소 구조

```text
src/
  TestOverlay.App/
    MainWindow.xaml(.cs)                  메인 보정 및 제어 UI
    LayoutEditorWindow.xaml(.cs)          오버레이 레이아웃 편집기
    OverlayWindow.xaml(.cs)               실제 click-through 오버레이
    OverlayPlacementPreviewWindow.xaml(.cs)
    BenchmarkWindow.xaml(.cs)             렌더러 벤치마크 UI
    LogWindow.xaml(.cs)                   세션 로그 뷰어
    Models/                               앱 상태와 프로필 모델
    Native/                               Win32, WinRT, Direct3D, DXGI interop
    Services/                             캡쳐, 감지, 프로필, 설정, 로그 서비스
tools/
  TestOverlay.DetectionProbe/             감지 테스트/분석용 도구
docs/
  README.ko.md                            한국어 README
  PLANNING.md                             로컬 계획 문서
  MVP_MANUAL_TEST.md                      수동 테스트 메모
```

## 빌드

필요 조건:

- Windows 10 2004 이상
- .NET 8 SDK

앱 빌드:

```powershell
dotnet build G:\gpt\git\testoverlayproj\src\TestOverlay.App\TestOverlay.App.csproj
```

Detection probe 빌드:

```powershell
dotnet build G:\gpt\git\testoverlayproj\tools\TestOverlay.DetectionProbe\TestOverlay.DetectionProbe.csproj
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
  -o G:\gpt\git\testoverlayproj\artifacts\MabinogiOverlay-0.0.2-beta
```

권장 패키지 구조:

```text
MabinogiOverlay-0.0.2-beta/
  Mabinogi Overlay.exe
  README.md
  save/
```

Portable 빌드는 쓰기 가능한 폴더에서 실행하는 것을 권장합니다. `Program Files`처럼 쓰기 권한이 제한되는 위치에서는 `settings.json`과 `save` 폴더 생성이 막힐 수 있습니다.

## 패키징 전 수동 스모크 테스트

공개 빌드 전 다음 항목을 확인합니다.

- Auto capture가 마비노기 `Client.exe` 창을 찾는지 확인
- Manual capture가 WGC로 선택한 마비노기 창을 캡쳐하는지 확인
- Settings에서 capture backend와 renderer mode가 변경되는지 확인
- 밝은 맵과 어두운 맵에서 Auto detect section 실행
- 가로로 긴 ROI는 상단 가로형으로, 세로로 긴 ROI는 세로형으로 라우팅되는지 확인
- Capture Preview에서 우클릭으로 드래그/디텍트 선택이 취소되는지 확인
- 선택한 슬롯을 기존 overlay slot을 지우지 않고 추가할 수 있는지 확인
- Layout Editor에서 global slot scale, per-slot scale, opacity, grid snap, undo/redo 확인
- Screen Preview로 실제 게임 화면에 맞게 위치 조정
- 오버레이 실행 후 게임 위에 계속 보이는지 확인
- 오버레이가 포커스를 빼앗거나 마우스 클릭을 먹지 않는지 확인
- Overlay stop과 stop hotkey 동작 확인
- Portable `save` 폴더 기준으로 프로필 저장/로드 확인
- 언어 변경 후 보이는 UI 문구가 즉시 갱신되는지 확인
- Settings > Log에서 세션 로그가 열리는지 확인

## 안전 경계

이 프로젝트는 다음을 의도적으로 하지 않습니다.

- 게임 메모리 읽기
- 프로세스 인젝션
- DLL 인젝션
- 게임 DirectX/OpenGL 렌더링 훅
- 게임 입력 자동화
- 안티치트 우회 또는 은닉 동작

공식 Windows 캡쳐 API와 별도 투명 오버레이 창만 사용합니다.
