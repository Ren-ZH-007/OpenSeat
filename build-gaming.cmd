@echo off
setlocal
cd /d "%~dp0"
set "OPEN_SEAT_CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%OPEN_SEAT_CSC%" exit /b 1
if not exist dist mkdir dist
"%OPEN_SEAT_CSC%" /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ /out:dist\OpenSeat.Gaming.exe /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll native\Gaming.cs native\GamingTests.cs
if errorlevel 1 exit /b 1
echo Build complete: dist\OpenSeat.Gaming.exe
exit /b 0
