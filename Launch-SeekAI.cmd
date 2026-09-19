@echo off
cd /d "%~dp0"
if exist "artifacts\SeekAI\SeekAI.exe" (
  start "" "artifacts\SeekAI\SeekAI.exe"
) else (
  dotnet run --project src\SeekAI
)
