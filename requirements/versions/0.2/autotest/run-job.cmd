@echo off
REM ============================================================================
REM run-job.cmd - the session bridge for remote / AI-driven runs.
REM
REM SSH lands in session 0, where there is no desktop, so a UI automation script
REM started that way would see no windows. This script is launched by the
REM scheduled task "WireCabinet-AutoTest" (run as HT, "only when the user is
REM logged on"), which puts it inside the interactive session where the HMI lives.
REM
REM Protocol (all files sit next to this script):
REM   job.txt   one line:  <steps.json>|<outdir>     written by the caller
REM   job.done  written by this script, contains the driver exit code
REM   job.log   console output of the run
REM
REM The task itself is triggered on demand (schtasks /Run), never on a schedule.
REM ============================================================================
setlocal EnableExtensions
set "HERE=%~dp0"
set "JOB=%HERE%job.txt"
set "DONE=%HERE%job.done"
set "LOG=%HERE%job.log"

if exist "%DONE%" del "%DONE%" >nul 2>&1
if not exist "%JOB%" (echo MISSING_JOB> "%HERE%job.error" & exit /b 2)

REM The job file was written just before the task was triggered; give the filesystem
REM a moment, then consume it so the next trigger cannot accidentally re-run it.
ping -n 2 127.0.0.1 >nul 2>&1

set "LINE="
set /p LINE=<"%JOB%"
del "%JOB%" >nul 2>&1
if not defined LINE (echo EMPTY_JOB> "%HERE%job.error" & exit /b 2)

for /f "tokens=1,2 delims=|" %%a in ("%LINE%") do (
    set "STEPS=%%a"
    set "OUTDIR=%%b"
)

REM Record the real session id: 1 (or any >0) means we are inside the interactive
REM session where the HMI lives; 0 means session 0 and no window can be automated.
for /f %%s in ('powershell -NoProfile -Command "(Get-Process -Id $PID).SessionId" 2^>nul') do set "SID=%%s"

echo [%DATE% %TIME%] job: %STEPS% -> %OUTDIR% sessionid=%SID% > "%LOG%"
call "%HERE%run-on-cabinet.cmd" "%STEPS%" "%OUTDIR%" >> "%LOG%" 2>&1
set "RC=%ERRORLEVEL%"
echo %RC% > "%DONE%"
echo [%DATE% %TIME%] done rc=%RC% >> "%LOG%"
endlocal & exit /b %RC%
