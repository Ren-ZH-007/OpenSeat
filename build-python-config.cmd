@echo off
cd /d "%~dp0"
py -3 -m venv .venv
if errorlevel 1 goto failed
.venv\Scripts\python.exe -m pip install pyinstaller
if errorlevel 1 goto failed
.venv\Scripts\python.exe -m PyInstaller --noconfirm --clean --windowed --onedir --name OpenSeat app.py
if errorlevel 1 goto failed
echo Build complete. Open dist\OpenSeat\OpenSeat.exe. Distribute the entire OpenSeat folder.
pause
exit /b 0
:failed
echo Build failed. Review the error above.
pause
exit /b 1
