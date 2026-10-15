@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
rem App name (Korean) lives in app.name so this file stays ASCII-only.
set /p APPNAME=<app.name
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%\csc.exe" set FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
set CSC=%FW%\csc.exe
if not exist "%CSC%" (
  echo csc.exe not found.
  exit /b 1
)

set SRC=src\Version.cs src\Models.cs src\GroupStore.cs src\ShellLink.cs src\IconHelper.cs src\GroupIcon.cs src\EntryIconCache.cs src\Knowledge.cs^
  src\ShortcutSync.cs src\Placement.cs src\MouseHook.cs src\TaskbarUia.cs src\ProcessScan.cs src\Arranger.cs src\DropOverlayForm.cs src\InputDialog.cs ^
  src\EntryListView.cs src\GroupListBox.cs src\FlyoutForm.cs src\ConfiguratorForm.cs src\AgentContext.cs src\WebCheck.cs src\MongUpdate.cs src\MongUpdateUi.WinForms.cs src\Program.cs

set REFS=/r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /r:System.Web.Extensions.dll /r:Microsoft.CSharp.dll ^
  /lib:"%FW%\WPF" /r:UIAutomationClient.dll /r:UIAutomationTypes.dll /r:WindowsBase.dll

set OPTS=/nologo /target:winexe /optimize+ /platform:anycpu /codepage:65001

rem Do not kill running instances: rename locked files to .old and build a fresh exe (restart the app to apply)
powershell -NoProfile -ExecutionPolicy Bypass -File tools\free_locked.ps1 "%CD%" "%APPNAME%.exe" TaskbarGroup.exe

rem Instant-placement DLL: unload it from explorer (so the file can be replaced), then rebuild it
rem (the DLL has a worker thread: ask it to unhook and leave by itself via a stop file)
if exist hook\placer.dll ( echo 1>hook\placer.stop & ping -n 3 127.0.0.1 >nul & del hook\placer.stop >nul 2>&1 )
rem (one-time: an older placer.dll without the stop file support is unloaded the old way)
if exist hook\inject.exe hook\inject.exe unload placer.dll >nul 2>&1
if exist hook\build_placer.bat cmd /c "hook\build_placer.bat"

rem If the app icon is missing, build once without it and let the exe generate it
if not exist res\app.ico (
  "%CSC%" %OPTS% /out:"%APPNAME%.exe" %REFS% %SRC%
  if errorlevel 1 goto fail
  start "" /wait "%APPNAME%.exe" --make-app-icon res\app.ico
  if not exist res\app.ico goto fail
)

"%CSC%" %OPTS% /win32icon:res\app.ico /out:"%APPNAME%.exe" %REFS% %SRC%
if errorlevel 1 goto fail
echo BUILD OK: %APPNAME%.exe
exit /b 0

:fail
echo BUILD FAILED
exit /b 1
