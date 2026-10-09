@echo off
REM ============================================================================
REM run-on-cabinet.cmd - run a UI scenario on the cabinet machine itself.
REM
REM The HMI is a WPF app, so UI Automation only works from a process that lives in
REM the interactive session of the logged-on user. Two ways to get that:
REM   - a person at the machine (double-click / RDP / VNC) runs this file directly;
REM   - remote/AI-driven runs go through a scheduled task ("run only when user is
REM     logged on") that invokes this same file - see ..\README.md section 3.
REM
REM Usage:  run-on-cabinet.cmd <steps.json> [outdir]
REM Output: result.json + PNG screenshots in outdir, plus EXITCODE on stdout.
REM ============================================================================
setlocal EnableExtensions
set "STEPS=%~1"
set "OUTDIR=%~2"
if "%STEPS%"=="" (
  echo usage: run-on-cabinet.cmd ^<steps.json^> [outdir]
  exit /b 2
)
if not exist "%STEPS%" (
  echo steps file not found: %STEPS%
  exit /b 2
)
if "%OUTDIR%"=="" set "OUTDIR=%TEMP%\autotest\run-%RANDOM%%RANDOM%"
if not exist "%OUTDIR%" mkdir "%OUTDIR%" >nul 2>&1

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0ui\uidriver.ps1" -Steps "%STEPS%" -OutDir "%OUTDIR%"
set "RC=%ERRORLEVEL%"
echo EXITCODE=%RC%
echo OUTDIR=%OUTDIR%
endlocal & exit /b %RC%
