# 단계 0 · 기술 검증 보고

**상태: 완료 (2026-11-07, 기획자 직접 확인 — 이동·점프·활공·대시, Iris Xe 낮음 옵션 30fps 이상, 다중 접속 표시, 치트 보정 전부 통과)**

## 만든 것

| 영역 | 파일 | 내용 |
|------|------|------|
| 공용 코드 | `shared/Runtime/` | 바이너리 프로토콜(Hello, MoveState, Snapshot, Spawn/Despawn, PositionCorrection, Ping/Pong, Notice), 위치 cm 양자화, `GameConfig` 스키마, 테스트 지형 함수(서버·클라 공용) |
| 데이터 | `data/shared/game_config.json` | 이동(달리기 6 m/s, 점프 1.5 m, 더블 점프 1.3 m, 활공, 대시 5 m·쿨 2초), 검증 허용치, 네트워크(20 Hz), 테스트 맵 |
| 서버 | `server/src/Game.Server/` | .NET 8, WebSocket `/ws`, 지역 Zone 20 Hz 틱, 이동 검증·보정, 닉네임 중복·길이 검사, 유휴 타임아웃, `/status`, WebGL 빌드 정적 서빙 |
| 테스트 | `server/tests/` | 이동 검증(순간이동·2배속·대시 쿨·점프 높이·땅 뚫기·맵 밖) + 프로토콜 왕복 |
| 봇 | `server/tools/BotClient/` | 가짜 플레이어 N명 (원 그리며 이동·점프), `--cheat`로 순간이동 치트 |
| 클라이언트 | `client/Assets/_Game/` | 부트스트랩, WebSocket(에디터/WebGL 공용 인터페이스), 3인칭 카메라, 이동(WASD·더블 점프·활공·대시), 원격 플레이어 100 ms 보간, 품질 3단계, FPS 오버레이, 테스트 맵(지형 청크 + 나무 300그루 병합) |
| WebGL | `client/Assets/WebGLTemplates/RpgTemplate/`, `Plugins/WebGL/websocket.jslib` | 한글 닉네임 HTML 입력창(Unity WebGL 한글 IME 문제 회피), DPR 상한(`?dpr=`), 메시지 표시 |
| 에디터 도구 | `client/Assets/_Game/Editor/ProjectSetup.cs` | 메뉴 **RPG > 1. 프로젝트 설정 적용** / **2. WebGL 빌드** |
| 실행 | `tools/run-server.cmd`, `run-bots.cmd`, `run-tests.cmd`, `setup_client.py` | |

## 검증 결과 (2026-11-05, 이 PC)

| 항목 | 결과 |
|------|------|
| 서버 빌드 | 경고 0, 오류 0 |
| 자동 테스트 | **12/12 통과** (이동 검증 8, 프로토콜 4) |
| 봇 3명 + 치트 봇 | 3명 모두 접속·이동, 치트 봇의 30 m 순간이동만 `TooFast`로 보정됨 |
| 정직한 봇 20명, 30초 | **잘못된 보정 0건** (점프 포함) |
| 서버 틱 처리 시간 | 20명일 때 1틱 0.01 ms 미만 (목표 50 ms) |
| Unity 클라이언트 | 컴파일 오류 0건(Unity 6000.3.28f1). 첫 컴파일에서 `Ping` 이름 충돌 1건을 고침 |
| WebGL 빌드 | 성공, 69.7 MB (`builds/webgl`) |
| 브라우저 접속 (개발 환경 브라우저) | 닉네임 입력 → 접속 → 지형·나무·내 캐릭터 표시, 봇 3명과 함께 `players 4`, 서버 보정 0건 |
| Iris Xe 30fps | **통과** (기획자 PC에서 직접 확인. 구체적인 FPS 숫자는 기록 안 함 → 단계 1 끝에 다시 측정하며 숫자를 남길 것) |

## 실행·테스트 방법

### A. 서버와 봇 (지금 바로 가능)
1. 탐색기에서 `tools\run-server.cmd` 더블클릭 → `listening: http://localhost:7777`가 나오면 준비 완료.
   - 처음 실행할 때 Windows 방화벽 창이 뜨면 **개인 네트워크 허용** (같은 와이파이의 다른 기기에서 접속할 때 필요).
2. 브라우저에서 http://localhost:7777/status → `players` 숫자 확인.
3. `tools\run-bots.cmd` 더블클릭 → 봇 3명 접속. `/status` 새로고침하면 `players: 3`.
4. 치트 확인: 명령 프롬프트에서 `tools\run-bots.cmd 3 --cheat` → 5초마다 `서버 보정: TooFast`가 찍히고 서버 창에 `WARN correct`가 나옴.
5. 자동 테스트: `tools\run-tests.cmd` → `통과! 실패: 0`.

### B. Unity 클라이언트 (설치·빌드 완료됨 → 아래 5번부터)
1. Unity Hub 설치 → 로그인 → **Installs > Install Editor > Unity 6 LTS** (모듈에서 **WebGL Build Support** 체크).
2. Claude에게 "Unity 설치했어"라고 알려 주면 `tools/setup_client.py`로 `client` 폴더를 Unity 프로젝트로 만들어 둠.
3. Unity Hub > **Add > Add project from disk** > `rpg-game-a\client` 선택해서 열기.
4. 메뉴 **RPG > 1. 프로젝트 설정 적용** → Stage0 씬이 열림. (입력 방식을 바꿨다는 경고가 뜨면 에디터 재시작)
5. 서버를 켠 상태에서 ▶ Play → 이름 입력 → Connect. 봇을 켜 두면 주황색 캡슐들이 돌아다님.
6. 메뉴 **RPG > 2. WebGL 빌드** (처음엔 10~20분 걸림) → 브라우저에서 http://localhost:7777 → 닉네임 입력 → 접속.
7. **창을 2~4개 나란히** 띄워서 서로 움직이는 게 보이는지 확인. (탭을 여러 개 쓰면 브라우저가 뒤쪽 탭을 멈춰서 그 탭 캐릭터가 멈춰 보임)
8. **Iris Xe 측정**: 품질 Low(기본)에서 왼쪽 위 `FPS`가 30 이상(초록색)인지, 맵을 한 바퀴 돌며 `worst(5s)` 값을 기록. F10으로 Medium/High도 기록.

### 조작
WASD 이동 · Space 점프(공중에서 한 번 더 = 더블 점프) · 공중에서 Space 길게 = 활공 · Shift 대시(쿨 2초) · 클릭 = 마우스 잠금, Esc = 해제 · 휠 = 줌 · F10 품질 · F3 정보 숨김

## 남은 문제

1. ~~Unity 컴파일 미검증~~ → 해결됨.
2. ~~Iris Xe 30fps 측정 전~~ → 통과.
3. **한글 폰트**: WebGL 화면 안 글자는 영어. 닉네임 입력·서버 알림은 HTML로 처리해서 한글 OK. Noto Sans KR 다운로드 허락이 필요함.
4. .NET SDK는 관리자 권한 없이 사용자 폴더(`%LOCALAPPDATA%\Microsoft\dotnet`)에 설치함. 실행 스크립트가 알아서 찾음.
5. 단계 0 단순화(단계 1·4에서 교체): 테스트 지형은 수식 지형 → 단계 1에서 실제 씬 높이맵, 모든 플레이어에게 모두 전송 → 단계 4에서 관심 영역(AOI).

## 다음 단계 계획
- Unity 설치 → 클라이언트 컴파일·에디터 플레이 → WebGL 빌드 → Iris Xe 30fps 측정 → **단계 0 완료 보고**.
- 그 뒤 단계 1(시작 마을): PostgreSQL 설치, DB 스키마, 데이터 테이블 + 검증기, 이장 NPC, 토끼·늑대, 퀘스트, 경험치, 평타 전투.
