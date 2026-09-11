@echo off
echo ============================================================
echo   Starting DeskShare - All Components
echo ============================================================
echo.

REM Start SignalingServer in new window
echo [1/3] Starting SignalingServer...
start "RemoteDesktop - SignalingServer" cmd /k "cd src\SignalingServer && dotnet run"
timeout /t 3 /nobreak > nul

REM Start ScreenSenderApp in new window
echo [2/3] Starting ScreenSenderApp...
start "RemoteDesktop - ScreenSenderApp" cmd /k "cd src\ScreenSenderApp && dotnet run"
timeout /t 2 /nobreak > nul

REM Start WebClient (open in browser)
echo [3/3] Opening WebClient in browser...
timeout /t 1 /nobreak > nul
start "" "src\WebClient\index.html"

echo.
echo ============================================================
echo   All components started!
echo.
echo   SignalingServer: https://localhost:7240 / http://localhost:5151
echo   ScreenSenderApp: Console window (choose option 3)
echo   WebClient: Browser (paste Server ID from ScreenSenderApp)
echo ============================================================
echo.
echo Press any key to exit this window...
pause > nul
