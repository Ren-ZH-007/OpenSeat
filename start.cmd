@echo off
setlocal
cd /d "%~dp0"
call build-native.cmd
if errorlevel 1 (
  pause
  exit /b 1
)
start "OpenSeat" "%~dp0dist\OpenSeat.exe"
