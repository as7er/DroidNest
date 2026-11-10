@echo off
setlocal
set "DROIDNEST_PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "DROIDNEST_PS=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
"%DROIDNEST_PS%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Check-Environment.ps1" -Launch
set "DROIDNEST_EXIT=%ERRORLEVEL%"
if not "%DROIDNEST_EXIT%"=="0" (
    echo.
    echo DroidNest did not start. See the checks above and README.md.
    pause
)
exit /b %DROIDNEST_EXIT%
