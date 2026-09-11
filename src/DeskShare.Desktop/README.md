# RemoteDesktop.Desktop - WPF Desktop Application

**Modern desktop klient pro DeskShare** s dual-mode rozhraním (Server + Client).

## ✨ Features

### Server Mode
- **One-Click Server Start**: Simple button to start screen sharing server
- **Auto-Generated Server ID**: Easy-to-share 9-character ID (format: ABC-DEF-GHI)
- **Optional Password Protection**: Generate secure 8-character password
- **Live Statistics**: Real-time connection count and uptime tracking
- **Status Indicators**: Visual status badges (Running/Stopped/Error)
- **Copy to Clipboard**: Quick copy buttons for ID and password
- **Server Settings**:
  - Require password for connections
  - Minimize to system tray
  - Auto-start server on launch

### Client Mode
- **Saved Connections**: Persistent list of frequently used connections
- **Quick Connect**: Double-click or button click to connect
- **Manual Connection**: Connect to any server with ID and optional password
- **Connection Management**:
  - Save new connections with custom names
  - Remove saved connections
  - View last used timestamp
- **Password Prompt**: Secure password dialog for protected connections

### Projection Window (Viewer)
- **Fullscreen Support**: F11 or button toggle for fullscreen mode
- **Auto-Hide Toolbar**: Clean viewing experience with on-hover toolbar
- **Live Statistics Display**:
  - FPS (frames per second)
  - Latency in milliseconds
  - Video quality/resolution
- **Keyboard Shortcuts**:
  - F11: Toggle fullscreen
  - ESC: Exit fullscreen or disconnect
- **Connection Status Overlay**: Visual feedback during connection process

## Architecture

### Project Structure

```
RemoteDesktop.Desktop/
├── MainWindow.xaml/.cs         # Main dual-mode window
├── App.xaml/.cs                # Application entry + DI setup
├── Windows/
│   └── ProjectionWindow.xaml/.cs   # Fullscreen viewer
├── Dialogs/
│   └── PasswordDialog.xaml/.cs     # Password input dialog
├── Services/
│   └── ConnectionManager.cs        # Connection persistence
└── README.md
```

### Technologies Used

- **WPF** (.NET 8): Windows Presentation Foundation for UI
- **Microsoft.Extensions.DependencyInjection**: Dependency injection
- **Microsoft.Extensions.Hosting**: Application hosting
- **Serilog**: Structured logging to file

### Design Patterns

- **MVVM-Light**: Code-behind with minimal logic, delegating to services
- **Dependency Injection**: Logger and services injected via DI container
- **Async/Await**: Non-blocking UI operations
- **Event-Driven**: UI updates via event handlers

## Usage

### Running the Application

```bash
cd src/RemoteDesktop.Desktop
dotnet run
```

### Server Mode Workflow

1. Open application → "Server Mode" tab
2. Click "Start Server"
3. Share Server ID and Password (if enabled) with remote user
4. Monitor active connections in real-time
5. Click "Stop Server" when done

### Client Mode Workflow

**First-Time Connection:**
1. Open application → "Client Mode" tab
2. Enter Server ID in "Manual Connection" section
3. Enter Password (if required)
4. Check "Save this connection" and provide a name
5. Click "Connect to Server"
6. Projection window opens in fullscreen

**Connecting to Saved Server:**
1. Open application → "Client Mode" tab
2. Find server in "Saved Connections" list
3. Click "Connect" button
4. Enter password if prompted
5. Projection window opens automatically

### Projection Window Controls

- **Move mouse to top**: Show toolbar with stats and controls
- **F11**: Toggle fullscreen mode
- **ESC**: Exit fullscreen (or disconnect if in windowed mode)
- **Disconnect button**: Close connection and return to main window

## Configuration

### Saved Connections Storage

Connections are saved to:
```
%APPDATA%\DeskShare\connections.json
```

### Logs Location

Application logs are written to:
```
%APPDATA%\DeskShare\logs\desktop-YYYY-MM-DD.log
```

Logs are:
- Rotated daily
- Retained for 7 days
- Structured with timestamp, level, message

## Integration Points (TODOs)

The following integration points are marked with `// TODO:` comments:

### Server Mode (MainWindow.xaml.cs)

```csharp
// TODO: Start actual server components (ScreenSenderApp, SignalingServer)
// Line 71-72 in StartServer_Click()

// TODO: Stop actual server components
// Line 114 in StopServer_Click()
```

**Implementation Steps:**
1. Reference ScreenSenderApp and SignalingServer projects
2. Start processes or in-process services
3. Pass Server ID to services
4. Monitor connection count and update UI

### Client Mode (MainWindow.xaml.cs)

```csharp
// TODO: Implement actual connection logic
// Line 381-382 in ConnectToServerAsync()
```

**Implementation Steps:**
1. Use WebRTC signaling to establish connection
2. Exchange SDP and ICE candidates
3. Open ProjectionWindow with active connection

### Projection Window (ProjectionWindow.xaml.cs)

```csharp
// TODO: Implement actual WebRTC connection
// Line 57-58 in ConnectToServerAsync()

// TODO: Start receiving video frames and rendering to VideoImage
// Line 83 in StartVideoRendering()

// TODO: Cleanup WebRTC connection
// Line 171 in OnClosed()

// TODO: Implement frame rendering methods
// Lines 174-184 (OnFrameReceived, OnConnectionStats)
```

**Implementation Steps:**
1. Create WebRTC peer connection
2. Receive video track from remote
3. Decode frames and render to WPF Image control
4. Update statistics (FPS, latency, quality)
5. Handle mouse/keyboard input forwarding

## Building for Release

### Development Build

```bash
dotnet build src/RemoteDesktop.Desktop/RemoteDesktop.Desktop.csproj --configuration Debug
```

### Release Build

```bash
dotnet build src/RemoteDesktop.Desktop/RemoteDesktop.Desktop.csproj --configuration Release
```

### Publish Standalone

```bash
dotnet publish src/RemoteDesktop.Desktop/RemoteDesktop.Desktop.csproj `
  --configuration Release `
  --output publish/Desktop `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true
```

This creates a single executable with embedded .NET runtime.

## UI Design

### Color Scheme

- **Primary Blue**: #0078D4 (Microsoft Blue)
- **Success Green**: #D4EDDA (background), #155D27 (text)
- **Warning Yellow**: #FFF3CD (background), #856404 (text)
- **Danger Red**: #D13438
- **Neutral Gray**: #F5F5F5 (background), #666666 (text)

### Typography

- **Headers**: 28px Bold
- **Subheaders**: 16-18px SemiBold
- **Body**: 14px Regular
- **Monospace** (IDs/Passwords): Consolas font family

### Layout

- **Cards**: White background with rounded corners (8px radius)
- **Buttons**: Rounded corners (4px), 20px horizontal padding
- **Status Badges**: Rounded pill shape (12px radius)

## Best Practices

### Performance

- Async operations for all network/file I/O
- UI updates via Dispatcher for thread safety
- Auto-dispose timers and resources in OnClosed

### Security

- Passwords stored in PasswordBox (secure string)
- No plaintext password persistence
- Clipboard operations with try-catch

### UX

- Visual feedback for actions (button content changes, status badges)
- Confirmation dialogs for destructive actions
- Auto-hide elements for clean fullscreen experience
- Keyboard shortcuts for power users

## Troubleshooting

### Application Won't Start

Check logs at `%APPDATA%\DeskShare\logs\desktop-*.log`

### Saved Connections Missing

Check if `%APPDATA%\DeskShare\connections.json` exists and is valid JSON

### Logger Errors

Ensure app has write permissions to `%APPDATA%\DeskShare\`

## Future Enhancements

- [ ] System tray minimization
- [ ] Multi-monitor support in projection window
- [ ] Clipboard synchronization
- [ ] File transfer UI
- [ ] Connection quality indicator
- [ ] Bandwidth usage statistics
- [ ] Auto-reconnect on disconnect
- [ ] Connection history with timestamps
- [ ] Favorite connections (pin to top)
- [ ] Dark mode theme

## License

Part of DeskShare project.

---

**Built with ❤️ using WPF and .NET 8**
