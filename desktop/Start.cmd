@echo off
cd /d "%~dp0"
if not exist "..\artifacts\desktop\conversation-manager-desktop.exe" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build.ps1"
  if errorlevel 1 (pause & exit /b 1)
)
start "" "..\artifacts\desktop\conversation-manager-desktop.exe"
