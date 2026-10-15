@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
rem App name (Korean) lives in app.name so this file stays ASCII-only.
set /p APPNAME=<app.name
rem Build the exe (build.bat also rebuilds hook\placer.dll) and make the installer (setup\<name>-Setup-<version>.exe).
call build.bat
if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
"%ISCC%" /DSrcDir="%~dp0." installer\%APPNAME%.iss || (echo SETUP FAILED & exit /b 1)
echo RELEASE OK: setup\
exit /b 0
