# Mabinogi Overlay

Mabinogi Overlay는 마비노기 퀵슬롯 중 원하는 칸을 별도의 항상 위 오버레이로 복제해서 보여주는 portable Windows 유틸리티입니다.

게임 클라이언트를 수정하지 않고 쿨타임 시인성을 높이는 것이 목적입니다. 게임 메모리 읽기, 코드 인젝션, 렌더링 훅, 입력 자동화는 하지 않습니다.

## 면책조항

Mabinogi Overlay는 비공식 유틸리티 프로그램이며 Nexon과 제휴, 승인, 지원 관계가 없습니다. 사용자는 본인이 이용하는 게임 서비스 지역의 규정을 확인하고 본인 책임하에 사용해야 합니다.

현재 버전: `0.0.2-beta`

이 프로그램은 OpenAI Codex 및 ChatGPT의 도움을 받아 개발되었습니다.

## 이 앱이 하는 일

- 마비노기 클라이언트 창을 캡쳐합니다.
- 사용자가 지정한 화면 영역에서 퀵슬롯 섹션을 감지합니다.
- 잘못 감지된 슬롯은 직접 수정할 수 있습니다.
- 선택한 슬롯을 별도 오버레이 레이아웃에 추가합니다.
- 오버레이는 게임 위에 표시되지만 마우스 클릭은 게임으로 통과시킵니다.
- 레이아웃과 슬롯 후보를 portable 프로필로 저장합니다.
- 영어와 한국어 UI를 지원합니다.

## 기본 사용 흐름

1. 마비노기를 실행하고 복제할 퀵슬롯을 화면에 띄웁니다.
2. Mabinogi Overlay를 실행합니다.
3. Auto capture 또는 Manual capture로 게임 화면을 프리뷰에 불러옵니다.
4. Auto detect section을 누르고 퀵슬롯 섹션 영역을 드래그합니다.
5. 필요한 슬롯을 선택해서 오버레이에 추가합니다.
6. Manage Layout에서 위치, 크기, 배치를 조정합니다.
7. 오버레이를 시작합니다.

## 주요 기능

- 마비노기 `Client.exe` 창 자동 탐색
- 직접 창을 고르는 Manual WGC capture
- WGC, DXGI, GDI 캡쳐 방식 선택
- GPU/DXGI, 개선 CPU/composited, 기존 CPU/WPF 렌더러 선택
- ROI 기반 퀵슬롯 섹션 감지
- ROI 모양에 따른 가로형/세로형 자동 라우팅
- 수동 슬롯 후보 생성, 이동, 크기 조정, 삭제, 다중선택
- 슬롯별 배율과 투명도 override
- 전역 투명도, 전역 슬롯 배율, grid snap, max FPS, stop hotkey 설정
- 실제 모니터 위에서 위치를 맞추는 Screen Preview
- 문제 확인용 세션 로그 뷰어
- 저장 위치를 지정할 수 있는 portable 프로필

## 기술 스택

- **언어:** C#
- **런타임:** .NET 8
- **UI:** WPF, WPF-UI
- **플랫폼:** Windows
- **캡쳐:** Windows Graphics Capture, DXGI Desktop Duplication, GDI BitBlt
- **그래픽 interop:** Direct3D 11, DXGI, Direct2D, DirectComposition
- **Native 연동:** Win32 window styles, global hotkey registration, click-through overlay behavior
- **저장:** `System.Text.Json` 기반 JSON 프로필과 설정

## 참고 사항

이 앱은 beta 버전입니다. 기능들에 버그가 있을 수 있으며, 버그 제보는 issue에 부탁드립니다.

WGC 캡쳐는 Windows 동작에 따라 노란 캡쳐 테두리가 보일 수 있습니다. DXGI와 GDI도 비교용으로 제공하지만, 설정 단계에서 선택한 게임 창만 안정적으로 캡쳐하는 데에는 WGC가 가장 유리합니다.

## 안전 경계

Mabinogi Overlay는 다음을 의도적으로 하지 않습니다.

- 게임 메모리 읽기
- 프로세스 인젝션
- DLL 인젝션
- 게임 렌더러 훅
- 게임 입력 자동화
- 안티치트 우회 또는 은닉 동작
- 패킷 감청

Windows 캡쳐 API와 별도 투명 오버레이 창만 사용합니다.
