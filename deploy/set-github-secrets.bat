@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0set-github-secrets.ps1"
pause
