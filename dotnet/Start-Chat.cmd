@echo off

cd /d "%~dp0Bonsai.Chat"
dotnet run --configuration Release --no-launch-profile --urls http://localhost:5080
