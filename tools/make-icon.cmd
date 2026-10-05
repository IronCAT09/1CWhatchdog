@echo off
rem Rebuilds assets\app.ico from the PNG files in assets (only needed when the icons change).
setlocal
cd /d "%~dp0.."

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

"%CSC%" /nologo /codepage:65001 /out:"%TEMP%\MakeIcon.exe" /r:System.Drawing.dll tools\MakeIcon.cs || exit /b 1
"%TEMP%\MakeIcon.exe" assets
del "%TEMP%\MakeIcon.exe"
