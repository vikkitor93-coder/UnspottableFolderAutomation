@echo off
setlocal
cd /d "%~dp0"
if not exist ".local" mkdir ".local"
echo stop>".local\STOP"
echo Stop requested. Running commands will be cancelled; automatic mode stays off.
echo To start again, double-click START.cmd yourself.
pause
