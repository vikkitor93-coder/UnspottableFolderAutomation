@echo off
setlocal
cd /d "%~dp0"
where py >nul 2>nul
if not errorlevel 1 (
  py -3 launcher.py
) else (
  python launcher.py
)
if errorlevel 1 echo Runner stopped or setup is needed. Read the message above.
pause
