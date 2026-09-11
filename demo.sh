#!/bin/bash

# DeskShare Demo Launcher
# This script starts the SignalingServer and opens a browser for testing

WITH_SCREEN_SENDER=false
SHOW_HELP=false

# Parse arguments
for arg in "$@"; do
    case $arg in
        --with-screen-sender)
            WITH_SCREEN_SENDER=true
            shift
            ;;
        --help|-h)
            SHOW_HELP=true
            shift
            ;;
        *)
            ;;
    esac
done

if [ "$SHOW_HELP" = true ]; then
    echo -e "\033[0;36mDeskShare Demo Launcher\033[0m"
    echo ""
    echo "Usage:"
    echo "  ./demo.sh                        Start SignalingServer and open browser"
    echo "  ./demo.sh --with-screen-sender   Also start ScreenSenderApp automatically"
    echo "  ./demo.sh --help                 Show this help"
    echo ""
    echo "After running this script:"
    echo "  1. SignalingServer will start on http://localhost:5000"
    echo "  2. Browser will open to http://localhost:5000"
    echo "  3. Navigate to /statistics to see server stats"
    echo "  4. Open src/WebClient/index.html to test WebRTC streaming"
    echo ""
    echo "Press Ctrl+C to stop all services"
    exit 0
fi

echo -e "\033[0;32m"
echo "==========================================="
echo "  DeskShare Demo Launcher"
echo "==========================================="
echo -e "\033[0m"

# Check if .NET 8 is installed
echo -e "\033[0;33m[1/4] Checking .NET 8 SDK...\033[0m"
if command -v dotnet &> /dev/null; then
    DOTNET_VERSION=$(dotnet --version)
    echo -e "\033[0;32m  ✓ Found .NET $DOTNET_VERSION\033[0m"
else
    echo -e "\033[0;31m  ✗ .NET 8 SDK not found! Please install from https://dotnet.microsoft.com\033[0m"
    exit 1
fi

# Build solution
echo -e "\n\033[0;33m[2/4] Building solution...\033[0m"
dotnet build DeskShare.sln --configuration Release --verbosity quiet
if [ $? -ne 0 ]; then
    echo -e "\033[0;31m  ✗ Build failed!\033[0m"
    exit 1
fi
echo -e "\033[0;32m  ✓ Build successful\033[0m"

# Start SignalingServer in background
echo -e "\n\033[0;33m[3/4] Starting SignalingServer...\033[0m"
dotnet run --project src/SignalingServer/RemoteDesktop.SignalingServer.csproj --configuration Release --no-build &
SIGNALING_PID=$!
echo -e "\033[0;32m  ✓ SignalingServer started (PID: $SIGNALING_PID)\033[0m"

# Wait for server to start
echo -e "\n\033[0;36m  Waiting for server to be ready...\033[0m"
sleep 3

# Test if server is responding
if curl -s http://localhost:5000/health > /dev/null 2>&1; then
    echo -e "\033[0;32m  ✓ Server is responding!\033[0m"
else
    echo -e "\033[0;33m  ⚠ Server may not be ready yet, continuing anyway...\033[0m"
fi

# Optionally start ScreenSenderApp (Note: Windows-only)
if [ "$WITH_SCREEN_SENDER" = true ]; then
    echo -e "\n\033[0;33m[3.5/4] Starting ScreenSenderApp...\033[0m"
    if [[ "$OSTYPE" == "msys" ]] || [[ "$OSTYPE" == "win32" ]] || [[ "$OSTYPE" == "cygwin" ]]; then
        dotnet run --project src/ScreenSenderApp/RemoteDesktop.ScreenSenderApp.csproj --configuration Release --no-build &
        SENDER_PID=$!
        echo -e "\033[0;32m  ✓ ScreenSenderApp started (PID: $SENDER_PID)\033[0m"
    else
        echo -e "\033[0;33m  ⚠ ScreenSenderApp is Windows-only, skipping...\033[0m"
    fi
fi

# Open browser
echo -e "\n\033[0;33m[4/4] Opening browser...\033[0m"
if command -v xdg-open &> /dev/null; then
    xdg-open "http://localhost:5000" &> /dev/null
elif command -v open &> /dev/null; then
    open "http://localhost:5000"
elif command -v start &> /dev/null; then
    start "http://localhost:5000"
else
    echo -e "\033[0;33m  ⚠ Could not open browser automatically\033[0m"
    echo -e "\033[0;33m  Please open http://localhost:5000 manually\033[0m"
fi
echo -e "\033[0;32m  ✓ Browser opened to http://localhost:5000\033[0m"

# Show instructions
echo -e "\n\033[0;32m"
echo "==========================================="
echo "  Demo is running!"
echo "==========================================="
echo -e "\033[0m"
echo -e "\033[0;36mAvailable endpoints:\033[0m"
echo "  • Home:        http://localhost:5000"
echo "  • Health:      http://localhost:5000/health"
echo "  • Statistics:  http://localhost:5000/statistics"
echo "  • WebClient:   src/WebClient/index.html (open in browser)"
echo ""
echo -e "\033[0;33mPress Ctrl+C to stop all services\033[0m"
echo ""

# Cleanup function
cleanup() {
    echo -e "\n\n\033[0;33mStopping services...\033[0m"
    kill $SIGNALING_PID 2>/dev/null
    if [ ! -z "$SENDER_PID" ]; then
        kill $SENDER_PID 2>/dev/null
    fi
    echo -e "\033[0;32m✓ All services stopped\033[0m"
    exit 0
}

# Trap Ctrl+C
trap cleanup INT TERM

# Wait for processes
wait
