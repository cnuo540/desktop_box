@echo off
setlocal enabledelayedexpansion

set "REPO=%~dp0.."
pushd "%REPO%"
set "PF86=%ProgramFiles(x86)%"
if not defined PF86 set "PF86=C:\Program Files (x86)"
set "PF64=%ProgramFiles%"
if not defined PF64 set "PF64=C:\Program Files"
set "VSWHERE=!PF86!\Microsoft Visual Studio\Installer\vswhere.exe"
set "VSINSTALL="
if exist "!VSWHERE!" for /f "usebackq tokens=*" %%i in (`"!VSWHERE!" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSINSTALL=%%i"
if not defined VSINSTALL set "VSINSTALL=!PF86!\Microsoft Visual Studio\2022\BuildTools"
if not exist "!VSINSTALL!\VC\Auxiliary\Build\vcvars64.bat" set "VSINSTALL=!PF64!\Microsoft Visual Studio\2022\BuildTools"
if not exist "!VSINSTALL!\VC\Auxiliary\Build\vcvars64.bat" (
  echo [ERROR] Visual Studio C++ build tools not found.
  popd
  exit /b 1
)
call "!VSINSTALL!\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 (
  echo [ERROR] vcvars64.bat failed.
  popd
  exit /b 1
)

if not exist "release\DesktopBoxSetupCore.exe" (
  echo [ERROR] Missing release\DesktopBoxSetupCore.exe. Compile DesktopBox.iss first.
  popd
  exit /b 1
)
if not exist "release" mkdir "release"
if not exist "_testtmp" mkdir "_testtmp"

rc /nologo /i "_testtmp" /fo "_testtmp\DesktopBoxSetupBootstrap.res" "src\DesktopBox.InstallerBootstrapper\DesktopBoxSetupBootstrap.rc"
if errorlevel 1 (
  echo [ERROR] Resource compilation failed.
  popd
  exit /b 1
)

cl /nologo /O2 /EHsc /std:c++17 /utf-8 ^
  "src\DesktopBox.InstallerBootstrapper\DesktopBoxSetupBootstrap.cpp" ^
  "_testtmp\DesktopBoxSetupBootstrap.res" ^
  /Fe:"release\DesktopBoxSetup.exe" ^
  /link /SUBSYSTEM:WINDOWS /MANIFEST:NO shell32.lib user32.lib
set "CL_EXIT=%ERRORLEVEL%"
if not "%CL_EXIT%"=="0" (
  echo [ERROR] Bootstrapper compilation failed.
  popd
  exit /b %CL_EXIT%
)

del /q "_testtmp\DesktopBoxSetupBootstrap.res" "release\DesktopBoxSetupCore.exe" 2>nul
popd
exit /b 0
