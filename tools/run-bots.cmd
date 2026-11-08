@echo off
chcp 65001 >nul
rem 가짜 플레이어 접속. 사용: run-bots.cmd [인원수] [--cheat]
rem 예) run-bots.cmd 3          → 봇 3명
rem     run-bots.cmd 3 --cheat  → 봇1이 5초마다 순간이동 치트 (서버 보정 확인용)
cd /d "%~dp0.."
rem SDK가 들어 있는 사용자 폴더 설치본을 먼저 씀 (시스템 dotnet은 런타임만 있을 수 있음)
if exist "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" set "PATH=%LOCALAPPDATA%\Microsoft\dotnet;%PATH%"
set COUNT=%1
if "%COUNT%"=="" set COUNT=3
dotnet run --project server\tools\BotClient -c Release -- --count %COUNT% %2
set "CODE=%errorlevel%"
echo.
echo ===== 종료됨 (종료 코드 %CODE%). 위 내용을 확인하고 아무 키나 누르면 창이 닫혀 =====
pause >nul
