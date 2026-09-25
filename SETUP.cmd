@echo off
setlocal
echo Requires Python 3.11 or newer and GitHub CLI.
echo If missing, install them from https://www.python.org/downloads/ and https://cli.github.com/
echo Then reopen this file. No token should be pasted into a config or chat.
where gh >nul 2>nul
if errorlevel 1 (
  echo GitHub CLI is missing. Install it, then reopen SETUP.cmd.
  pause
  exit /b 1
)
gh auth login --hostname github.com --git-protocol https --web
if errorlevel 1 (
  echo Sign-in did not finish. Nothing was started.
) else (
  echo Setup finished. Extract the entire source ZIP, then double-click START.cmd.
)
pause
