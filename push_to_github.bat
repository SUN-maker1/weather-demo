@echo off
setlocal enabledelayedexpansion

echo ============================================
echo        Git Auto Push Script
echo   Repo: https://github.com/SUN-maker1/GAME-CREATEOR.git
echo ============================================
echo.

REM ---------- Step 0: Set Git global user config ----------
echo [Step 0/4] Setting Git global user.name and user.email ...
git config --global user.name "SUN-maker1"
git config --global user.email "1264257086@qq.com"
echo [OK] user.name=SUN-maker1  user.email=1264257086@qq.com
echo.

REM ---------- Step 1: git add . ----------
echo [Step 1/4] Running: git add .
git add .
set "ec=!errorlevel!"
if not "!ec!"=="0" (
    echo [FAIL] git add . failed! Errorcode: !ec!
    echo [Reason] Current folder is not a Git repo, or files are locked.
    pause
    exit /b 1
)
echo [OK] git add . done.
echo.

REM ---------- Step 2: input commit message ----------
echo [Step 2/4] Type your commit message:
set /p "commit_msg=  Message: "
if "!commit_msg!"=="" (
    echo [FAIL] Empty message. Cancelled.
    pause
    exit /b 1
)
echo.

REM ---------- Step 3: git commit ----------
echo [Step 3/4] Running: git commit -m "!commit_msg!"
git commit -m "!commit_msg!"
set "ec=!errorlevel!"
if "!ec!"=="0" (
    echo [OK] git commit done.
) else if "!ec!"=="1" (
    echo [INFO] Nothing to commit - will still try push.
) else (
    echo [FAIL] git commit failed! Errorcode: !ec!
    pause
    exit /b 1
)
echo.

REM ---------- Step 4: git push ----------
echo [Step 4/4] Pushing to GitHub ...
git push origin main
set "ec=!errorlevel!"
if not "!ec!"=="0" (
    echo.
    echo [FAIL] git push failed! Errorcode: !ec!
    echo [Possible reasons]
    echo   1. Network issue - check internet / proxy
    echo   2. Remote has new commits - run: git pull origin main --rebase
    echo   3. Token expired - regenerate at https://github.com/settings/tokens
) else (
    echo.
    echo ============================================
    echo   [DONE] Pushed to GitHub successfully!
    echo ============================================
)
echo.
pause
endlocal