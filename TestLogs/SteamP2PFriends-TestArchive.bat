@echo off
setlocal EnableExtensions DisableDelayedExpansion
title SteamP2PFriends Runtime Test Evidence

set "TOOLROOT=%~dp0"
set "ENGINE=%TOOLROOT%SteamP2PFriends-TestEvidence.ps1"
set "ARCHIVE=%TOOLROOT%artifacts"

if not exist "%ENGINE%" goto :missing
where powershell.exe >nul 2>nul
if errorlevel 1 goto :nopowershell
if not exist "%ARCHIVE%" mkdir "%ARCHIVE%"
if not exist "%ARCHIVE%" goto :archivefail

:menu
cls
echo SteamP2PFriends CFG and DLL Evidence
echo.
echo IMPORTANT: START with Unturned closed. FINISH after Unturned is closed.
echo No game log is copied, read, or evaluated.
echo Default evidence requires VerboseDiagnostics=false and RouteDiagnostics=false on both roles.
echo Evidence output: %ARCHIVE%
echo.
echo 1. START as Host
echo 2. START as Client
echo 3. FINISH as Host
echo 4. FINISH as Client
echo 5. VERIFY merged Case on Host
echo 6. Exit
echo.
set "ACTION="
set /p "ACTION=Select 1-6: "
if "%ACTION%"=="1" (
  set "ROLE=Host"
  goto :start
)
if "%ACTION%"=="2" (
  set "ROLE=Client"
  goto :start
)
if "%ACTION%"=="3" (
  set "ROLE=Host"
  goto :finish
)
if "%ACTION%"=="4" (
  set "ROLE=Client"
  goto :finish
)
if "%ACTION%"=="5" goto :verify
if "%ACTION%"=="6" goto :done
goto :menu

:start
call :askcase
if errorlevel 1 goto :menu
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ENGINE%" -Action Start -CaseId "%CASEID%" -Role %ROLE% -DiagnosticProfile Default
if errorlevel 1 goto :failed
echo.
echo START complete. Launch Unturned and execute one test route now.
pause
goto :menu

:finish
call :askcase
if errorlevel 1 goto :menu
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ENGINE%" -Action Finish -CaseId "%CASEID%" -Role %ROLE% -DiagnosticProfile Default
if errorlevel 1 goto :failed
echo.
echo FINISH complete for %ROLE%.
if /I "%ROLE%"=="Client" echo Transfer this CaseId's Client evidence into the Host case without overwriting files.
if /I "%ROLE%"=="Host" echo After Client evidence is merged, select VERIFY.
pause
goto :menu

:verify
call :askcase
if errorlevel 1 goto :menu
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ENGINE%" -Action Verify -CaseId "%CASEID%" -DiagnosticProfile Default
echo.
pause
goto :menu

:askcase
set "CASEID="
set /p "CASEID=Shared Case ID: "
if not defined CASEID exit /b 1
exit /b 0

:failed
echo.
echo This step failed. Read the exact reason above; existing evidence was not overwritten.
pause
goto :menu

:missing
echo SteamP2PFriends-TestEvidence.ps1 is missing beside this BAT file.
pause
exit /b 1

:nopowershell
echo Windows PowerShell was not found in PATH.
pause
exit /b 1

:archivefail
echo The TestLogs artifacts directory could not be created.
pause
exit /b 1

:done
endlocal
exit /b 0
