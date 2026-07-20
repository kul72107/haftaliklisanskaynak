@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%~dp0MYedekSetup.ps1"
exit /b %ERRORLEVEL%
