@echo off
cd /d "%~dp0"
where py >nul 2>nul
if errorlevel 1 (
  echo Python 3.10+ with tkinter is required. Install Python with the Python launcher.
  pause
  exit /b 1
)
py -3 app.py
if errorlevel 1 pause
