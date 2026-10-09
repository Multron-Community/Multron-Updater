@echo off
setlocal
cd /d "%~dp0"
set "OUT=%~dp0publish"

echo Building Multron Updater...

:closeapp
tasklist /fi "imagename eq MultronUpdater.exe" | find /i "MultronUpdater.exe" >nul || goto :build
echo.
echo Multron Updater is running - closing it...
taskkill /im MultronUpdater.exe /f >nul 2>&1
ping -n 2 127.0.0.1 >nul
tasklist /fi "imagename eq MultronUpdater.exe" | find /i "MultronUpdater.exe" >nul || goto :build

echo It runs as administrator - please accept the Windows (UAC) prompt to close it.
powershell -NoProfile -Command "try { Start-Process taskkill -ArgumentList '/im','MultronUpdater.exe','/f' -Verb RunAs -WindowStyle Hidden -Wait } catch { exit 1 }"
ping -n 2 127.0.0.1 >nul
tasklist /fi "imagename eq MultronUpdater.exe" | find /i "MultronUpdater.exe" >nul || goto :build

echo.
echo Could not close Multron Updater.
echo Right-click its tray icon and choose Exit, then press any key to try again.
pause >nul
goto :closeapp

:build
dotnet publish -c Release -o "%OUT%" || goto :builderror
echo.
echo Done: %OUT%\MultronUpdater.exe
pause
exit /b 0

:builderror
echo.
echo Build failed. See the messages above.
echo If the exe is still in use, close Multron Updater and run build.bat again.
pause
exit /b 1
