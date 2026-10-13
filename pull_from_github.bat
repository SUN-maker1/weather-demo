@echo off
setlocal enabledelayedexpansion

REM The runtime copy is launched with: /runtime "repository path"
if /i "%~1"=="/runtime" (
    set "REPO_DIR=%~2"
    goto runtime
)

REM ---------- Launcher: run from TEMP so this script can stash itself ----------
cd /d "%~dp0"
set "REPO_DIR=%CD%"
set "RUNTIME_SCRIPT=%TEMP%\pull_from_github_runtime_%RANDOM%.bat"

echo Preparing safe pull environment ...
copy /y "%~f0" "%RUNTIME_SCRIPT%" >nul
if errorlevel 1 (
    echo [FAIL] Could not create temporary runtime script:
    echo   %RUNTIME_SCRIPT%
    echo.
    pause >nul
    exit /b 1
)

call "%RUNTIME_SCRIPT%" /runtime "%REPO_DIR%"
set "RC=%ERRORLEVEL%"
del /q "%RUNTIME_SCRIPT%" >nul 2>nul
exit /b %RC%

:runtime
cd /d "%REPO_DIR%"

set "BRANCH=main"
set "HAS_CHANGES=0"
set "STASHED=0"
set "PULL_RC=0"
set "POP_RC=0"
set "FINAL_RC=0"
set "SKIP_STATUS=0"

echo ============================================
echo        Git Pull Script
echo   Repo: https://github.com/SUN-maker1/weather-demo.git
echo ============================================
echo.

REM ---------- Step 1: Check environment and local changes ----------
echo [Step 1/3] Checking Git environment and local changes ...

where git >nul 2>nul
if errorlevel 1 (
    echo [FAIL] Git is not installed or not found in PATH.
    set "FINAL_RC=1"
    set "SKIP_STATUS=1"
    goto final_result
)

git rev-parse --is-inside-work-tree >nul 2>nul
if errorlevel 1 (
    echo [FAIL] This folder is not a Git repository: %CD%
    set "FINAL_RC=1"
    set "SKIP_STATUS=1"
    goto final_result
)

for /f "delims=" %%i in ('git status --porcelain') do set "HAS_CHANGES=1"

if "!HAS_CHANGES!"=="1" (
    echo Found local changes. Saving them temporarily with git stash ...
    git stash push -u -m "auto-stash before pull"
    if errorlevel 1 (
        echo [FAIL] git stash failed. Pull was not started.
        set "FINAL_RC=1"
        goto final_result
    )
    set "STASHED=1"
) else (
    echo No local changes found.
)
echo.

REM ---------- Step 2: Pull latest code ----------
echo [Step 2/3] Pulling latest code from origin/!BRANCH! ...
REM -X theirs: if the pull merge hits a conflict, always take the incoming remote version
git pull -X theirs origin !BRANCH!
set "PULL_RC=!errorlevel!"
echo.

REM ---------- Step 3: Restore local changes ----------
echo [Step 3/3] Restoring local changes ...
if "!STASHED!"=="1" (
    git stash pop
    set "POP_RC=!errorlevel!"
    if not "!POP_RC!"=="0" (
        echo Conflicts while restoring local changes.
        echo Auto-resolving all conflicts in favor of remote origin/!BRANCH! ...
        set "CONFLICT_FOUND=0"
        for /f "delims=" %%f in ('git diff --name-only --diff-filter=U') do (
            set "CONFLICT_FOUND=1"
            echo   taking remote version: %%f
            git checkout --ours -- "%%f"
            git add -- "%%f"
        )
        if "!CONFLICT_FOUND!"=="1" (
            git stash drop "stash@{0}"
            echo [OK] Conflicts resolved using the remote version. Local edits to those conflicting parts were dropped.
            set "POP_RC=0"
            set "FINAL_RC=0"
        ) else (
            echo [WARN] stash pop failed for a non-content reason. The stash was kept.
        )
    )
) else (
    echo No stash to restore.
)
echo.

if not "!PULL_RC!"=="0" (
    echo [FAIL] git pull failed. Error code: !PULL_RC!
    echo.
    echo [Possible reasons]
    echo   1. Network cannot reach github.com
    echo   2. There are conflicts between local commits and remote
    echo   3. GitHub is temporarily unavailable
    echo.
    echo If conflicts are listed, fix the conflict markers, then run:
    echo   git add .
    echo   git commit -m "resolve pull conflicts"
    set "FINAL_RC=!PULL_RC!"
)

if not "!POP_RC!"=="0" (
    echo [FAIL] git stash pop failed. Your stashed changes were not deleted.
    echo.
    echo Fix the conflict markers, then run:
    echo   git add .
    echo   git stash drop
    if "!FINAL_RC!"=="0" set "FINAL_RC=!POP_RC!"
)

if "!FINAL_RC!"=="0" echo [OK] Pull complete. Your local code is up to date.

:final_result
echo.
if not "!SKIP_STATUS!"=="1" (
    echo Current status:
    git status
    echo.
)
echo ============================================
echo   Done. Press any key to close.
echo ============================================
pause >nul
endlocal & exit /b %FINAL_RC%
