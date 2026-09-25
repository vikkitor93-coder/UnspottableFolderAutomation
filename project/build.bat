@echo off
setlocal EnableExtensions
cd /d "%~dp0"
title Unspottable Expanded v0.9.8 - QA Verification

echo ============================================================
echo  Unspottable Expanded v0.9.8 - QA VERIFICATION
echo  Stable White Square core + opt-in process-local QA input
echo  Build + deploy + launch, NO PowerShell
echo ============================================================
echo.

set "GAME=D:\SteamLibrary\steamapps\common\Unspottable"
if not exist "%GAME%\Unspottable.exe" (
  echo Unspottable was not found at the remembered location:
  echo   %GAME%
  echo.
  set /p "GAME=Paste the folder containing Unspottable.exe: "
)
if not exist "%GAME%\Unspottable.exe" goto :NO_GAME

where dotnet >nul 2>nul
if errorlevel 1 goto :NO_DOTNET

set "UE_GAME_DIR=%GAME%"
set "MANAGED=%GAME%\Unspottable_Data\Managed"
set "CORE=%GAME%\BepInEx\core"
if not exist "%MANAGED%\UnityEngine.CoreModule.dll" goto :NO_REFS
if not exist "%MANAGED%\Rewired_Core.dll" goto :NO_REFS
if not exist "%CORE%\BepInEx.Core.dll" goto :NO_REFS

for /f "tokens=1 delims=." %%A in ('dotnet --list-sdks 2^>nul') do if %%A GEQ 8 set "HAS_SDK=1"
if not defined HAS_SDK goto :NO_DOTNET

tasklist /FI "IMAGENAME eq Unspottable.exe" 2>NUL | find /I "Unspottable.exe" >NUL
if not errorlevel 1 (
  echo Unspottable is already running. Close it and run this file again.
  echo.
  pause
  exit /b 1
)

echo Building against exact installed game assemblies:
echo   %GAME%
echo.

dotnet build UnspottableExpanded.csproj -c Release -o output --nologo
if errorlevel 1 goto :BUILD_FAILED

set "TARGETDIR=%GAME%\BepInEx\plugins\UnspottableExpanded"
set "TARGET=%TARGETDIR%\UnspottableExpanded.dll"
if not exist "%TARGETDIR%" mkdir "%TARGETDIR%"
copy /Y "%~dp0output\UnspottableExpanded.dll" "%TARGET%" >nul
if errorlevel 1 goto :DEPLOY_FAILED

fc /b "%~dp0output\UnspottableExpanded.dll" "%TARGET%" >nul
if errorlevel 1 goto :VERIFY_FAILED

(
  echo Unspottable Expanded 0.9.8 H2.4 NORMAL LIFECYCLE
  echo Vanilla menus, player selection, start flow and input preserved.
  echo Normal launch: safe White Square core only. QA launch: opt-in telemetry + process-local verification harness.
  echo Deployed %DATE% %TIME%
) > "%TARGETDIR%\DEPLOYED_VERSION.txt"

echo.
echo AUTO-DEPLOY VERIFIED.
if /I "%~1"=="--no-launch" goto :DEPLOY_ONLY
echo Launching Unspottable normally...
start "" /D "%GAME%" "%GAME%\Unspottable.exe"
echo.
echo ============================================================
echo SUCCESS - v0.9.8 INSTALLED AND GAME LAUNCHED
echo ============================================================
echo.
echo TEST:
echo   1. Use the normal Local menu.
echo   2. Join TWO players normally.
echo   3. Use the normal physical START area.
echo   4. Try Meadow first and verify movement.
echo   5. Then try Arena / White Square.
echo.
echo There is NO quick-start or solo-start code in this build.
echo.
timeout /t 4 >nul
exit /b 0

:DEPLOY_ONLY
echo.
echo ============================================================
echo SUCCESS - v0.9.8 BUILT AND INSTALLED (NOT LAUNCHED)
echo ============================================================
echo.
exit /b 0

:NO_GAME
echo ERROR: Unspottable.exe was not found.
if /I "%~1"=="--no-launch" exit /b 1
pause
exit /b 1
:NO_DOTNET
echo ERROR: .NET 8 SDK or newer is required.
if /I "%~1"=="--no-launch" exit /b 1
pause
exit /b 1
:NO_REFS
echo ERROR: Required Unity/BepInEx assemblies were not found under:
echo   %GAME%
if /I "%~1"=="--no-launch" exit /b 1
pause
exit /b 1
:BUILD_FAILED
echo.
echo BUILD FAILED. Existing installed DLL was not changed.
if /I "%~1"=="--no-launch" exit /b 1
pause
exit /b 1
:DEPLOY_FAILED
echo.
echo BUILD SUCCEEDED but DLL deployment failed.
if /I "%~1"=="--no-launch" exit /b 1
pause
exit /b 1
:VERIFY_FAILED
echo.
echo DLL copied but byte verification failed.
if /I "%~1"=="--no-launch" exit /b 1
pause
exit /b 1
