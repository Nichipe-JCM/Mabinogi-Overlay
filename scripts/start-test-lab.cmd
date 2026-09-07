@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-test-lab.ps1"
if errorlevel 1 pause
