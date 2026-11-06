@echo off
title Get newest Mega Hub
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Get-newest-Mega-Hub.ps1" %1
if errorlevel 1 (
  echo.
  echo Something went wrong. Take a screenshot of the text above and send it to me.
  pause
)
