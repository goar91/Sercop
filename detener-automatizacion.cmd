@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\stop-system.ps1" %*
if errorlevel 1 pause
