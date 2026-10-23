@echo off
setlocal
cd /d "%~dp0"
call build-native.cmd
if errorlevel 1 (
  pause
  exit /b 1
)
start /wait "OpenSeat self-test" "%~dp0dist\OpenSeat.exe" --self-test
if errorlevel 1 pause
