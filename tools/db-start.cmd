@echo off
chcp 65001 >nul
rem 개발용 PostgreSQL 켜기 (이 PC 전용, localhost:5433, 관리자 권한 필요 없음)
rem 처음 실행하면 DB 폴더를 만든다. run-server.cmd가 자동으로 부르므로 보통 직접 켤 필요 없음.
set "PGBIN=%LOCALAPPDATA%\RpgGame\pgsql\bin"
set "PGDATA=%LOCALAPPDATA%\RpgGame\pgdata"
if not exist "%PGBIN%\pg_ctl.exe" goto :missing
if exist "%PGDATA%\PG_VERSION" goto :start
echo [DB] 처음 실행: DB 폴더 만드는 중 %PGDATA%
"%PGBIN%\initdb.exe" -D "%PGDATA%" -U rpg -A trust -E UTF8 --locale=C >"%LOCALAPPDATA%\RpgGame\initdb.log" 2>&1
if errorlevel 1 goto :initfail
>>"%PGDATA%\postgresql.conf" echo listen_addresses = 'localhost'
>>"%PGDATA%\postgresql.conf" echo port = 5433
:start
"%PGBIN%\pg_ctl.exe" status -D "%PGDATA%" >nul 2>&1
if not errorlevel 1 goto :running
"%PGBIN%\pg_ctl.exe" start -D "%PGDATA%" -l "%PGDATA%\server.log" -w -t 30 >nul
if errorlevel 1 goto :startfail
echo [DB] 켜짐 localhost:5433
exit /b 0
:running
echo [DB] 이미 켜져 있음 localhost:5433
exit /b 0
:missing
echo [DB] PostgreSQL이 없어: %PGBIN%
echo      Claude에게 PostgreSQL 다시 깔아 달라고 말해 줘.
exit /b 1
:initfail
echo [DB] DB 폴더 만들기 실패. %LOCALAPPDATA%\RpgGame\initdb.log 확인
exit /b 1
:startfail
echo [DB] 시작 실패. %PGDATA%\server.log 확인
exit /b 1
