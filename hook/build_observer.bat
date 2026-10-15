@echo off
setlocal
cd /d "%~dp0"
rem Visual Studio 설치 위치와 버전 폴더 이름은 PC마다 다르므로 vcvars64.bat 을 찾아서 쓴다
set "VCVARS="
for /f "delims=" %%F in ('dir /b /s "C:\Program Files\Microsoft Visual Studio\*vcvars64.bat" 2^>nul') do set "VCVARS=%%F"
if not defined VCVARS for /f "delims=" %%F in ('dir /b /s "C:\Program Files (x86)\Microsoft Visual Studio\*vcvars64.bat" 2^>nul') do set "VCVARS=%%F"
call "%VCVARS%" >nul
if errorlevel 1 (echo vcvars failed & exit /b 1)
set MH=third_party\minhook
cl /nologo /utf-8 /O2 /LD /MT /EHsc /I%MH%\include observer.cpp %MH%\src\buffer.c %MH%\src\hook.c %MH%\src\trampoline.c %MH%\src\hde\hde64.c /Fe:observer.dll /link user32.lib
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo BUILD OK: observer.dll
