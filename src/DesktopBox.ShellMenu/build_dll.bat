@echo off
REM Build DesktopBox.ShellMenu.exe (isolated native C++ right-click menu host).
REM Portable: auto-locates MSVC via vswhere, uses repo-relative paths.
REM Run from a normal cmd (NOT the VS developer prompt) - this script sets up the env.
setlocal enabledelayedexpansion

REM --- Locate Visual Studio via vswhere (ships with VS Installer) ---
set "SYSDRIVE=%SystemDrive%"
if not defined SYSDRIVE set "SYSDRIVE=C:"
set "PF86=%ProgramFiles(x86)%"
if not defined PF86 set "PF86=%SYSDRIVE%\Program Files (x86)"
set "PF64=%ProgramFiles%"
if not defined PF64 set "PF64=%SYSDRIVE%\Program Files"

set "VSWHERE=%PF86%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" set "VSWHERE=%PF64%\Microsoft Visual Studio\Installer\vswhere.exe"

set "VSINSTALL="
if exist "%VSWHERE%" (
  for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSINSTALL=%%i"
)

REM --- vswhere missing (Build Tools installed without the VS Installer): scan known paths ---
REM Read the roots with delayed expansion inside the block: %ProgramFiles(x86)% must NOT be
REM expanded there, cmd would treat the ")" in the variable name as the end of the block.
set "VSROOT=%PF86%\Microsoft Visual Studio"
set "VSROOT64=%PF64%\Microsoft Visual Studio"
if not defined VSINSTALL (
  for %%p in (
    "!VSROOT!\2022\BuildTools"
    "!VSROOT!\2022\Community"
    "!VSROOT!\2022\Professional"
    "!VSROOT!\2022\Enterprise"
    "!VSROOT64!\2022\BuildTools"
    "!VSROOT!\2019\BuildTools"
    "!VSROOT!\2019\Community"
  ) do if not defined VSINSTALL if exist "%%~p\VC\Auxiliary\Build\vcvars64.bat" set "VSINSTALL=%%~p"
)

if not defined VSINSTALL (
  echo [ERROR] No MSVC C++ tools found. Install "Visual Studio Build Tools" with the C++ workload.
  exit /b 1
)

REM --- Load the x64 build environment ---
call "%VSINSTALL%\VC\Auxiliary\Build\vcvars64.bat"
if errorlevel 1 (echo [ERROR] vcvars64.bat failed & exit /b 1)

REM --- Resolve repo root (parent of this script's dir) and build ---
set "SCRIPTDIR=%~dp0"
pushd "%SCRIPTDIR%\..\.."
set "REPO=%CD%"
popd

if not exist "%REPO%\publish" mkdir "%REPO%\publish"
set "INTDIR=%REPO%\publish\.native-build"
if not exist "%INTDIR%" mkdir "%INTDIR%"

cl /nologo /O2 /EHsc /std:c++17 /utf-8 ^
  "%SCRIPTDIR%DesktopBox.ShellMenu.cpp" ^
  /Fo:"%INTDIR%\DesktopBox.ShellMenu.obj" ^
  /Fe:"%REPO%\publish\DesktopBox.ShellMenu.exe" ^
  /link ole32.lib shell32.lib user32.lib gdi32.lib ^
  /SUBSYSTEM:WINDOWS

set "CL_EXIT=%ERRORLEVEL%"
echo CL_EXIT=%CL_EXIT%
if "%CL_EXIT%"=="0" if exist "%INTDIR%" rmdir /s /q "%INTDIR%"
endlocal & exit /b %CL_EXIT%
