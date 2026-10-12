@echo off
cd /d "%~dp0"
if not exist "release\unified\CodexConversationManager.exe" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build.ps1"
  if errorlevel 1 (pause & exit /b 1)
  start "" "%~dp0src\CodexConversationManager\bin\Release\net48\CodexConversationManager.exe" %*
  exit /b
)
start "" "%~dp0release\unified\CodexConversationManager.exe" %*
