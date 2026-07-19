# Mabinogi Overlay

## 구버전에서 업데이트

- 업데이트 후 처음 실행하면 기존 실행 파일 옆의 `settings.json`, `save`, `Logs/app.log`를 LocalAppData 사용자 데이터 폴더로 복사합니다.
- 이미 LocalAppData에 파일이 있으면 덮어쓰지 않으며, 기존 portable 파일도 삭제하지 않습니다.
- 구버전 렌더러 조합이 현재 권장 조합과 같으면 자동 선택으로 전환하고, 사용자가 지정한 비표준 조합은 고급 수동 설정으로 보존합니다.
- 초기 버전의 `TuarimMonitorEnabled` 프로필 키도 읽을 수 있으며, 다음 저장부터 올바른 `TuairimMonitorEnabled` 키로 기록합니다.

Mabinogi Overlay는 마비노기 퀵슬롯 일부를 별도의 항상 위 오버레이에 표시하는 portable Windows 유틸리티입니다. 게임 클라이언트의 메모리를 읽거나, 코드를 주입하거나, 렌더러를 후킹하거나, 입력을 자동화하지 않습니다.

현재 공개 기준 버전은 `0.0.4.2`입니다. 버프/투아림 감시, 단축키 타이머, 에린 타이머와 다중 프로필 관리를 포함합니다.

## 면책 조항

이 프로그램은 비공식 유틸리티이며 Nexon과 제휴, 승인 또는 지원 관계가 없습니다. 사용 중인 게임 서비스 지역의 규정을 확인하고 본인의 책임 아래 사용해야 합니다.

이 프로그램은 OpenAI Codex 및 ChatGPT의 도움을 받아 개발되었습니다.

## 주요 기능

- `Client.exe`를 우선 탐색하는 게임 창 목록과 자동 창 캡처
- 직접 창을 선택하는 수동 WGC 캡처
- WGC, DXGI Desktop Duplication, GDI BitBlt 캡처 방식 선택
- 캡처 방식에 맞춘 렌더러 자동 선택과 문제 해결용 고급 수동 지정
- ROI 기반 가로형/세로형 퀵슬롯 섹션 인식
- 후보 슬롯 다중 선택, 이동, 크기 변경, 수동 추가, 섹션 관리
- 레이아웃 편집, 그리드 스냅, 슬롯별 투명도/배율 재정의
- 항상 위 표시, 클릭 통과, 포커스를 빼앗지 않는 오버레이
- 프로필별 자동 저장과 명시적 불러오기
- 영어/한국어 UI와 세션 로그 보기
- 선택형 버프 지속 시간/투아림 게이지 감시, 사운드 및 시각 알림
- 앱 내부 에린 시간과 영구 알람 목록

## 기본 사용 순서

1. 게임을 실행하고 복제할 퀵슬롯을 화면에 표시합니다.
2. Mabinogi Overlay에서 `자동 창 캡처` 또는 `수동 창 캡처`를 실행합니다.
3. `퀵슬롯 인식`을 누르고 퀵슬롯 섹션을 드래그합니다.
4. 후보 목록에서 필요한 슬롯을 선택한 뒤 `오버레이에 추가`를 누릅니다.
5. `레이아웃 관리`에서 캔버스와 슬롯 배치를 조정합니다.
6. `오버레이 시작`으로 실행하고, 설정한 단축키나 `오버레이 중지`로 종료합니다.

## 캡처 방식 참고

- **WGC:** 기본 캡처 방식이며 자동 설정에서는 GPU 가속 렌더러를 사용합니다. 지원되는 Windows에서는 테두리 없는 캡처 권한을 요청하고, 허용되면 노란 캡처 테두리를 숨깁니다. GPU 초기화에 실패하면 CPU 합성으로 전환됩니다.
- **DXGI:** 선택 창이 있는 모니터를 캡처한 뒤 게임 클라이언트 영역을 자릅니다. 자동 설정에서는 CPU 합성 렌더러를 사용하며, 다른 창이 게임 위를 가리면 그 화면도 반영될 수 있습니다.
- **GDI:** 호환성용 데스크톱 캡처 방식입니다.

## 저장 위치

기본 구성에서는 `%LocalAppData%\Mabinogi Overlay` 아래에 다음을 저장합니다.

- `settings.json`: 언어, 캡처 방식, 렌더러, 프로필 저장 경로
- `Profiles/<프로필명>.json`: 후보, 섹션, 레이아웃, 감시 설정
- `Logs/app.log`: 현재 앱 세션 로그

에린 타이머 설정은 `erin-timer.json`에 별도로 저장됩니다. 설정에서 프로필 저장 경로만 다른 위치로 변경할 수 있습니다.

## 개발 문서

- [Codex 프로젝트 인수인계](CODEX_PROJECT_HANDOFF.md)
- [기획 및 기술 방향](PLANNING.md)
- [수동 검증 체크리스트](MVP_MANUAL_TEST.md)
- [코드 서명 안내](CODE_SIGNING.md)

## 기술 스택

- C#, .NET 8, WPF, WPF-UI
- Windows Graphics Capture, DXGI Desktop Duplication, GDI BitBlt
- Direct3D 11, DXGI, Direct2D, DirectComposition
- Win32 click-through / no-activate / global hotkey 연동
- `System.Text.Json` 기반 설정과 프로필
- Windows OCR 기반 버프 시간·투아림 퍼센트 인식

## 라이선스

Mabinogi Overlay는 [MIT License](../LICENSE)로 배포됩니다. WPF-UI 및 Vortice 패키지를 포함한 외부 의존성은 각자의 라이선스를 따릅니다.
