@echo off
if exist "%~dp0Published\Bonsai.Chat.exe" (
  cd /d "%~dp0Published"
  Bonsai.Chat.exe
  exit /b
)
cd /d "%~dp0Bonsai.Chat"
dotnet run --configuration Release --no-launch-profile --urls http://localhost:5080
