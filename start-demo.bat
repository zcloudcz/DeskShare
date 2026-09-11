@echo off
echo ========================================
echo DeskShare - Quick Start Demo
echo ========================================
echo.
echo Starting components...
echo.

REM Start Signaling Server
echo [1/3] Starting Signaling Server...
start "RemoteDesktop - Signaling Server" cmd /k "cd src\SignalingServer && dotnet run"
timeout /t 5 /nobreak >nul

REM Start Screen Sender
echo [2/3] Starting Screen Sender...
start "RemoteDesktop - Screen Sender" cmd /k "cd src\ScreenSenderApp && dotnet run"
timeout /t 3 /nobreak >nul

REM Open WebClient in default browser
echo [3/3] Opening WebClient in browser...
start "" "%CD%\src\WebClient\index.html"

echo.
echo ========================================
echo All components started!
echo ========================================
echo.
echo Next steps:
echo 1. Wait for both console windows to finish loading
echo 2. In ScreenSenderApp console, press [3] to start WebRTC session
echo 3. Copy the Server ID shown in the console
echo 4. In the browser, verify Server ID matches (default: screen-sender-001)
echo 5. Click "Connect" button
echo 6. You should see your screen streaming!
echo.
echo To stop: Close all console windows or run stop-demo.bat
echo ========================================
pause
