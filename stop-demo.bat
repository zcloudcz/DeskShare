@echo off
echo ========================================
echo DeskShare - Stopping Demo
echo ========================================
echo.

echo Stopping Screen Sender...
taskkill /FI "WINDOWTITLE eq RemoteDesktop - Screen Sender*" /F >nul 2>&1

echo Stopping Signaling Server...
taskkill /FI "WINDOWTITLE eq RemoteDesktop - Signaling Server*" /F >nul 2>&1

echo.
echo ========================================
echo All components stopped!
echo ========================================
pause
