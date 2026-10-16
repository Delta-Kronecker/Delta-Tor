@echo off
rem DeltaTor tunnel DLL diagnostic. Double-click, then paste everything
rem this window shows into the bug report.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0diagnose.ps1"
echo.
pause
