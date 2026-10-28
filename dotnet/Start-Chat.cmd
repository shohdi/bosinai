@echo off
if exist "%~dp0Published\Bonsai.Chat.exe" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Sync-WebAssets.ps1"
  if errorlevel 1 exit /b 1
  cd /d "%~dp0Published"
  Bonsai.Chat.exe
  exit /b
)
cd /d "%~dp0Bonsai.Chat"
dotnet run --configuration Release --no-launch-profile --urls http://localhost:5080
