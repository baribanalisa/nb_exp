# Build Fix Instructions

## Issues Fixed

### Fixed Build Warnings:
1. **ShimmerGsrClient.cs:34** - Removed unused `_processStarted` field (CS0169)
2. **AnalysisWindow.xaml.cs:64** - Removed unused `_stimIsVideo` field (CS0414)  
3. **MainWindow.xaml.cs:762** - Removed unused `_isShuttingDown` field (CS0414)
4. **MainWindow.xaml.cs:867** - Fixed async method without await (CS1998)

### Root Cause of File Locking:
The main build-blocking issue occurs because:
- Previous app instances didn't close cleanly
- Background tasks and network connections weren't properly disposed
- Process 24916 kept the `.exe` file locked, preventing rebuild

## Resolution Steps:

### 1. Kill the Locked Process
Since the build shows `NeuroBureau.Experiment.exe (24916)` is locking the file:
```powershell
# Run in PowerShell as Administrator
Stop-Process -Id 24916 -Force
```

Or use Task Manager:
1. Open Task Manager → Details tab
2. Find process with PID 24916
3. Right-click → End Process Tree

### 2. Clean Build
```powershell
dotnet clean
dotnet build
```

### 3. Run Application
```powershell
dotrun
```

## For Future Prevention:
The app now includes proper resource cleanup:
- `ShimmerGsrClient` properly implements `IAsyncDisposable`
- All network connections are closed during shutdown
- Background tasks are cancelled and awaited
- Cancellation tokens are properly disposed

This should prevent the file locking issue from recurring.