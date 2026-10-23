@echo off
setlocal
cd /d "%~dp0"
set "OPEN_SEAT_CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%OPEN_SEAT_CSC%" (
  echo 64-bit .NET Framework C# compiler not found.
  echo Install or repair .NET Framework 4.x, then retry.
  exit /b 1
)
if not exist dist mkdir dist
"%OPEN_SEAT_CSC%" /nologo /target:winexe /platform:x64 /optimize+ /out:dist\OpenSeat.exe /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll native\OpenSeat.cs native\VmSeats.cs native\SelfTest.cs
if errorlevel 1 (
  echo Build failed. See compiler errors above.
  exit /b 1
)
echo Build complete: dist\OpenSeat.exe
exit /b 0
