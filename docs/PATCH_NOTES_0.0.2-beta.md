# Mabinogi Overlay 0.0.2-beta Patch Notes

기준 버전: `0.0.1-beta`

## 핵심 변경점

- 관리 UI를 어두운 테마 기반의 새 화면으로 재구성했습니다.
- 한국어 UI를 추가했습니다.
- 영어/한국어 문구를 외부 언어 파일로 분리했습니다.
- 퀵슬롯 인식 방식을 ROI 기반으로 개선했습니다.
- WGC 외에 DXGI, GDI 캡쳐 방식을 선택할 수 있게 했습니다.
- GPU/DXGI, 개선 CPU/Composited, 기존 CPU/WPF 렌더러를 선택할 수 있게 했습니다.
- 렌더러 부하를 비교하기 위한 벤치마크 창을 추가했습니다.

## UI 및 사용성

- 메인 창에 커스텀 타이틀 바와 앱 로고를 적용했습니다.
- Capture Preview가 비어 있을 때 캡쳐 대기 상태를 표시합니다.
- Slot Candidates 영역의 선택, 체크박스, 버튼 배치를 정리했습니다.
- Create manual section을 팝업 방식으로 바꾸고 토글 동작을 안정화했습니다.
- Capture Preview와 오른쪽 설정 영역 사이에 크기 조절 분할선을 추가했습니다.
- Layout Editor 기본 크기와 편집 공간을 확대했습니다.
- Layout Editor에서 그리드 스냅, 다중 선택, 방향키 미세 이동, 선택 삭제를 개선했습니다.
- Screen Preview에서 실제 화면 위에 오버레이 위치를 맞출 수 있습니다.
- Settings 창에서 프로필, 언어, 캡쳐 방식, 렌더러, 로그, 벤치마크 설정을 관리합니다.
- 세션 로그 확인 창을 추가했습니다.

## Localization

- UI 언어로 영어와 한국어를 지원합니다.
- 번역 문자열을 C# 코드가 아니라 `Localization/en-US.lang`, `Localization/ko-KR.lang` 파일에서 읽습니다.
- 패키징된 앱에서도 언어 파일을 직접 수정해서 문구를 조정할 수 있습니다.
- 언어 파일에 없는 항목은 영어 또는 key 표시로 fallback됩니다.

## Capture

- Auto capture와 Manual capture를 분리했습니다.
- Auto capture는 마비노기 `Client.exe` 창을 자동 탐색합니다.
- Manual capture는 사용자가 WGC 창 선택을 직접 진행합니다.
- 런타임 캡쳐 방식으로 WGC, DXGI Desktop Duplication, GDI BitBlt를 선택할 수 있습니다.
- WGC 캡쳐 생성 실패 및 창 선택 실패 처리를 보강했습니다.
- 오버레이 실행 중에는 Manage Layout을 열지 못하도록 차단했습니다.

## Overlay Renderer

- GPU/DXGI 기반 라이브 오버레이 렌더러를 추가했습니다.
- 개선 CPU/Composited 렌더러를 추가했습니다.
- 기존 CPU/WPF 렌더러도 선택지로 유지합니다.
- 기본 렌더러 설정과 캡쳐 방식 설정을 Settings에서 저장합니다.
- Max FPS 선택과 렌더러별 부하 테스트를 지원합니다.

## Quickslot Detection

- 기존 detect 흐름을 ROI 기반 퀵슬롯 섹션 인식으로 개선했습니다.
- ROI의 가로/세로 비율에 따라 가로형/세로형 섹션을 자동 라우팅합니다.
- 어두운 맵에서 잘못된 어두운 배경을 슬롯으로 잡는 문제를 줄였습니다.
- 1px 고정 테두리 특성을 반영해 슬롯 후보 평가를 개선했습니다.
- 가로형 슬롯의 F키/숫자 표시 영역을 고려하도록 보정했습니다.
- 세로형 슬롯은 숫자/키 표시가 없는 특성을 반영해 평가 방식을 조정했습니다.
- 탐지 과정의 진단 로그를 세션 로그에 남기도록 개선했습니다.

## Profile and Settings

- 프로필 저장 위치를 Settings에서 지정할 수 있습니다.
- portable 빌드에서는 실행 파일 옆의 `save` 폴더를 기본 저장 위치로 사용합니다.
- 오버레이에 등록된 슬롯뿐 아니라 Slot Candidates도 프로필에 저장합니다.
- 전역 opacity, slot scale, max FPS, stop hotkey, grid snap 설정 저장을 보강했습니다.
- 슬롯별 opacity와 scale override를 지원합니다.

## Fixes

- 실제 오버레이가 마우스 클릭과 포커스를 가져가는 문제를 개선했습니다.
- 드래그 중 포커스가 끊길 때 선택 사각형이 남는 문제를 수정했습니다.
- 수동 섹션 팝업이 닫혔다가 다시 열리는 토글 문제를 수정했습니다.
- Opacity 슬라이더 직접 클릭 시 값이 최소/최대로 튀는 문제를 수정했습니다.
- 전역 slot scale이 0.5 이하로 실제 반영되지 않던 문제를 수정했습니다.
- 후보 슬롯 삭제, 추가, 다중 선택, undo/redo 동작을 보강했습니다.

## Known Notes

- 이 버전은 beta입니다. 기능에 버그가 있을 수 있습니다.
- WGC는 Windows 동작에 따라 캡쳐 테두리가 보일 수 있습니다.
- DXGI/GDI는 테두리가 보이지 않는 대신, 창이 가려진 상황에서는 프리뷰 캡쳐 품질이 WGC보다 제한될 수 있습니다.
- 퀵슬롯 인식은 캡쳐 환경, 해상도, UI 배율, 맵 밝기에 영향을 받을 수 있으며 최종 위치는 수동 보정할 수 있습니다.
