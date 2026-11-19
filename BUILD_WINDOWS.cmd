@echo off
setlocal EnableExtensions DisableDelayedExpansion
title GigaPMC v2.0.1 - Windows Build
cd /d "%~dp0"
if errorlevel 1 goto :directory_error

set "GIGAPMC_BUILD_RC=1"
set "GIGAPMC_BUILD_LOG=%~dp0BUILD_LAST.log"
set "GIGAPMC_CANDIDATE="
set "GIGAPMC_PATH_FILE="
if defined LOCALAPPDATA set "GIGAPMC_PATH_FILE=%LOCALAPPDATA%\GigaPMC\game-root.txt"

> "%GIGAPMC_BUILD_LOG%" echo GigaPMC v2.0.1 Windows launcher started.

echo ==========================================================
echo   GigaPMC v2.0.1 - build client, server and release ZIP
echo ==========================================================
echo Source: "%CD%"
echo Log:    "%GIGAPMC_BUILD_LOG%"
echo.

if not exist "%~dp0BUILD.ps1" goto :missing_build
if not exist "%~dp0GAME_REFERENCES_SHA256.csv" goto :missing_references
where powershell.exe >nul 2>nul
if errorlevel 1 goto :missing_powershell

rem Priority: drag-and-drop/argument, environment, saved path, C:\SPT, prompt.
if "%~1"=="" goto :try_environment
set "GIGAPMC_CANDIDATE=%~1"
call :check_game_root
if not errorlevel 1 goto :game_found
echo The folder passed to the launcher is not a valid SPT game root.

:try_environment
if not defined GIGAPMC_GAME_ROOT goto :try_saved
set "GIGAPMC_CANDIDATE=%GIGAPMC_GAME_ROOT%"
call :check_game_root
if not errorlevel 1 goto :game_found
echo Saved environment path is no longer valid.

:try_saved
if not defined GIGAPMC_PATH_FILE goto :try_default
if not exist "%GIGAPMC_PATH_FILE%" goto :try_default
set "GIGAPMC_CANDIDATE="
for /f "usebackq delims=" %%G in ("%GIGAPMC_PATH_FILE%") do set "GIGAPMC_CANDIDATE=%%~G"
call :check_game_root
if not errorlevel 1 goto :game_found
echo Previously selected game folder is no longer valid.

:try_default
set "GIGAPMC_CANDIDATE=C:\SPT"
call :check_game_root
if not errorlevel 1 goto :game_found

:prompt_for_game
set "GIGAPMC_CANDIDATE="
echo.
echo SPT 4.1.6 was not found automatically.
echo Enter the folder containing EscapeFromTarkov_Data and SPT_Runtime.
echo Example: D:\Games\SPT
echo You can copy the folder path from File Explorer.
echo Press ENTER without typing anything to cancel.
set /p "GIGAPMC_CANDIDATE=SPT game folder: "
if not defined GIGAPMC_CANDIDATE goto :cancelled
call :check_game_root
if not errorlevel 1 goto :game_found
echo.
echo ERROR: The selected folder is not a valid SPT game root.
echo Expected: EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll
echo       and SPT_Runtime\SPTarkov.Server.Core.dll
goto :prompt_for_game

:check_game_root
if not defined GIGAPMC_CANDIDATE exit /b 1
rem File-system paths cannot contain quotes; allow users to paste quoted paths.
set "GIGAPMC_CANDIDATE=%GIGAPMC_CANDIDATE:"=%"
if not defined GIGAPMC_CANDIDATE exit /b 1
if not exist "%GIGAPMC_CANDIDATE%\EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll" exit /b 1
if not exist "%GIGAPMC_CANDIDATE%\SPT_Runtime\SPTarkov.Server.Core.dll" exit /b 1
exit /b 0

:game_found
echo.
echo SPT folder: "%GIGAPMC_CANDIDATE%"
echo NOTE: BUILD.ps1 will verify game DLL SHA-256 hashes before building.
>> "%GIGAPMC_BUILD_LOG%" echo Selected SPT game folder: "%GIGAPMC_CANDIDATE%"

rem Keep this user-specific path outside the source repository.
if not defined GIGAPMC_PATH_FILE goto :skip_save
if not exist "%LOCALAPPDATA%\GigaPMC" mkdir "%LOCALAPPDATA%\GigaPMC" >nul 2>nul
if not exist "%LOCALAPPDATA%\GigaPMC" goto :skip_save
> "%GIGAPMC_PATH_FILE%" echo "%GIGAPMC_CANDIDATE%"
if errorlevel 1 echo WARNING: Could not remember the folder. The build can still proceed.

:skip_save
echo.
echo Running BUILD.ps1 ...
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0BUILD.ps1" -GameRoot "%GIGAPMC_CANDIDATE%" >> "%GIGAPMC_BUILD_LOG%" 2>&1
set "GIGAPMC_BUILD_RC=%ERRORLEVEL%"
echo ==========================================================
echo Build output:
echo ==========================================================
type "%GIGAPMC_BUILD_LOG%"
echo.
if "%GIGAPMC_BUILD_RC%"=="0" goto :success
echo FAIL: Build exited with code %GIGAPMC_BUILD_RC%.
echo See BUILD_LAST.log and any artifacts\build-* logs.
goto :finish

:success
echo PASS: Build script finished successfully.
echo Verified P08 release ZIP is exported to your Downloads folder.
echo The detailed build logs remain under artifacts\build-*.
goto :finish

:missing_build
set "GIGAPMC_ERROR=BUILD.ps1 not found. Extract the complete source archive first."
goto :error
:missing_references
set "GIGAPMC_ERROR=GAME_REFERENCES_SHA256.csv is missing."
goto :error
:missing_powershell
set "GIGAPMC_ERROR=Windows PowerShell was not found."
goto :error
:cancelled
set "GIGAPMC_ERROR=Game folder selection cancelled. No build was started."
goto :error
:error
echo ERROR: %GIGAPMC_ERROR%
>> "%GIGAPMC_BUILD_LOG%" echo ERROR: %GIGAPMC_ERROR%
goto :finish
:directory_error
echo ERROR: Cannot open the extracted source directory.
set "GIGAPMC_BUILD_RC=1"
goto :finish
:finish
echo.
echo This window stays open until you press a key.
pause >nul
exit /b %GIGAPMC_BUILD_RC%
