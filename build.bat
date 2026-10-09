@echo off
setlocal
cd /d "%~dp0"
set "TARGET=%~dp0..\MultronUpdater.exe"

echo Building Multron Updater...
dotnet publish -c Release -o "%~dp0publish" || goto :builderror

:closeapp
tasklist /fi "imagename eq MultronUpdater.exe" | find /i "MultronUpdater.exe" >nul || goto :copy
echo.
echo Multron Updater is running - closing it...
taskkill /im MultronUpdater.exe /f >nul 2>&1
ping -n 2 127.0.0.1 >nul
tasklist /fi "imagename eq MultronUpdater.exe" | find /i "MultronUpdater.exe" >nul || goto :copy

echo It runs as administrator - please accept the Windows (UAC) prompt to close it.
powershell -NoProfile -Command "try { Start-Process taskkill -ArgumentList '/im','MultronUpdater.exe','/f' -Verb RunAs -WindowStyle Hidden -Wait } catch { exit 1 }"
ping -n 2 127.0.0.1 >nul
tasklist /fi "imagename eq MultronUpdater.exe" | find /i "MultronUpdater.exe" >nul || goto :copy

echo.
echo Could not close Multron Updater.
echo Right-click its tray icon and choose Exit, then press any key to try again.
pause >nul
goto :closeapp

:copy
copy /y "%~dp0publish\MultronUpdater.exe" "%TARGET%" >nul || goto :copyerror
echo.
echo Done: %TARGET%
pause
exit /b 0

:builderror
echo.
echo Build failed (compile error). See the messages above.
pause
exit /b 1

:copyerror
echo.
echo Could not copy the new exe to %TARGET%
echo Make sure Multron Updater is closed, then run build.bat again.
pause
exit /b 1
