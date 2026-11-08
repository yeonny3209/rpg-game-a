@echo off
chcp 65001 >nul
rem 서버 자동 테스트 실행
cd /d "%~dp0.."
rem SDK가 들어 있는 사용자 폴더 설치본을 먼저 씀 (시스템 dotnet은 런타임만 있을 수 있음)
if exist "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" set "PATH=%LOCALAPPDATA%\Microsoft\dotnet;%PATH%"
dotnet test server\GameServer.sln
set "CODE=%errorlevel%"
echo.
echo ===== 종료됨 (종료 코드 %CODE%). 위 내용을 확인하고 아무 키나 누르면 창이 닫혀 =====
pause >nul
