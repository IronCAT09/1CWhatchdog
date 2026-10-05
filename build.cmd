@echo off
rem Builds both editions:
rem   bin\1CWhatchdog.exe          - full: services + program launch control
rem   bin\1CWhatchdog-services.exe - services only (SERVICES_ONLY)
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist bin mkdir bin

call :build bin\1CWhatchdog.exe "" || exit /b 1
call :build bin\1CWhatchdog-services.exe "/define:SERVICES_ONLY" || exit /b 1
exit /b 0

:build
"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ %~2 ^
  /out:%1 ^
  /win32manifest:app.manifest ^
  /win32icon:assets\app.ico ^
  /resource:assets\app.ico,OneCWhatchdog.app.ico ^
  /resource:assets\icon_about_256x256.png,OneCWhatchdog.about.png ^
  /r:System.ServiceProcess.dll /r:System.Management.dll ^
  /r:System.Runtime.Serialization.dll /r:System.Xml.dll ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  src\*.cs
if errorlevel 1 exit /b 1
echo OK: %1
exit /b 0
