@echo off
rem HSaiHelper: download the free local AI (llama.cpp + Gemma 4 model, 5-17 GB). Runs Setup-AI.ps1.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup-AI.ps1" %*
echo.
pause
