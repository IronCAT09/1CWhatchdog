@echo off
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist bin mkdir bin

"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ ^
  /out:bin\1CWhatchdog.exe ^
  /win32manifest:app.manifest ^
  /win32icon:assets\app.ico ^
  /resource:assets\app.ico,OneCWhatchdog.app.ico ^
  /resource:assets\icon_about_256x256.png,OneCWhatchdog.about.png ^
  /r:System.ServiceProcess.dll /r:System.Management.dll ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  src\*.cs
if errorlevel 1 exit /b 1

echo OK: bin\1CWhatchdog.exe
