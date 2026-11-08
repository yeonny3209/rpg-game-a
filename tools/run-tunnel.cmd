@echo off
chcp 65001 >nul
rem 친구들이 링크로 들어올 수 있게 내 PC의 서버(7777)를 Cloudflare 터널로 연다. 서버(run-server.cmd)가 켜져 있어야 해.
rem 아래에 나오는 https://xxxx.trycloudflare.com 주소를 친구들에게 알려 주면 돼. 이 창을 닫거나 PC를 끄면 링크도 멈추고, 다시 켜면 주소가 바뀐다.
cd /d "%~dp0cloudflared"
if not exist cloudflared.exe (
  echo cloudflared.exe 가 없어. https://github.com/cloudflare/cloudflared/releases 에서 cloudflared-windows-amd64.exe 를 받아 tools\cloudflared\cloudflared.exe 로 저장해 줘.
  pause
  exit /b 1
)
cloudflared.exe tunnel --no-autoupdate --url http://localhost:7777
pause
