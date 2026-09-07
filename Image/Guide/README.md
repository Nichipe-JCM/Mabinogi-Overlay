# 가이드 이미지 파일 규칙

가이드의 각 단계에 마우스를 올렸을 때 표시할 PNG 파일을 이 폴더에 둡니다.
파일이 없는 단계는 이미지 팝업을 표시하지 않습니다.

## 추가 방법

1. 안내하려는 화면을 캡처하고 계정명, 캐릭터명, 채팅 등 개인정보를 가립니다.
2. 이미지를 PNG로 저장합니다.
3. 이 폴더(`Image/Guide`)에 아래 파일명 규칙으로 넣습니다.
4. 프로젝트 파일 수정은 필요 없습니다. `TestOverlay.App.csproj`가 이 폴더의 모든 PNG를 자동으로 리소스에 포함합니다.
5. 앱을 다시 빌드한 뒤 가이드의 해당 단계 문장 위에 마우스를 올려 확인합니다.

## 규칙

- 형식: PNG
- 파일명: `{topic-id}-{step-number:00}.png`
- 단계 번호는 1이 아니라 `01`, `02`처럼 두 자리로 작성
- 권장 크기: 800x500px 안팎
- 앱 표시 최대 크기: 440x300px(원본 비율 유지)
- 파일이 없거나 이름이 다르면 오류 없이 이미지 팝업만 표시되지 않음
- 같은 파일명을 교체한 뒤에는 앱을 다시 빌드해야 함

## 필요한 파일

- `quick-start-01.png` ~ `quick-start-05.png`
- `profile-capture-01.png` ~ `profile-capture-04.png`
- `quickslots-01.png` ~ `quickslots-04.png`
- `layout-01.png` ~ `layout-04.png`
- `monitors-01.png` ~ `monitors-04.png`
- `custom-timers-01.png` ~ `custom-timers-04.png`
- `erin-timer-01.png` ~ `erin-timer-03.png`
- `compact-mode-01.png` ~ `compact-mode-03.png`
- `settings-01.png` ~ `settings-04.png`
- `troubleshooting-01.png` ~ `troubleshooting-04.png`

예를 들어 빠른 시작의 첫 번째 단계 이미지는
`Image/Guide/quick-start-01.png`이고, 프로필/캡처의 세 번째 단계는
`Image/Guide/profile-capture-03.png`입니다.
