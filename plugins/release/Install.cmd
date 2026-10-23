@echo off
rem HSaiHelper: install the plugin into Hearthstone Deck Tracker (runs Install.ps1, no admin rights needed).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1"
echo.
pause
