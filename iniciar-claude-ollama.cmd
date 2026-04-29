@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\start-claude-code-ollama.ps1" %*
if errorlevel 1 pause

