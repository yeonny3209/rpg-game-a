@echo off
chcp 65001 >nul
rem 개발용 PostgreSQL 끄기
"%LOCALAPPDATA%\RpgGame\pgsql\bin\pg_ctl.exe" stop -D "%LOCALAPPDATA%\RpgGame\pgdata" -m fast
