@echo off
chcp 65001 >nul
rem 게임 서버 실행 (http://localhost:7777). 종료: Ctrl+C
cd /d "%~dp0.."
rem SDK가 들어 있는 사용자 폴더 설치본을 먼저 씀 (시스템 dotnet은 런타임만 있을 수 있음)
if exist "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" set "PATH=%LOCALAPPDATA%\Microsoft\dotnet;%PATH%"
call "%~dp0db-start.cmd"
if errorlevel 1 goto :end
dotnet run --project server\src\Game.Server -c Release
set "CODE=%errorlevel%"
:end
echo.
echo ===== 종료됨 (종료 코드 %CODE%). 위 내용을 확인하고 아무 키나 누르면 창이 닫혀 =====
pause >nul
