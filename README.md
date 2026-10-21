# MyMusic

MyMusic is a Windows desktop music player focused on a local, managed music library with a modern WinUI 3 interface.

> **Development status:** V1.0 is currently being stabilized. This repository is intended to remain private until the release build and installer have been fully tested.

## Features

- Local managed music library with metadata and embedded artwork
- Albums, artists, playlists, favorites, search, sorting, and queue management
- Metadata and cover-art editing
- Fullscreen player
- Shuffle, repeat, seek, volume, and mute controls
- Windows audio-output selection
- Chromecast playback
- Dark/light themes and selectable accent
- System-tray support
- Configurable managed-library location
- Installer and uninstaller with optional preservation of user data

## Requirements

To build MyMusic:

- Windows 11 x64
- .NET 10 SDK
- Windows App SDK dependencies restored through NuGet

To build the installer, install **Inno Setup 6** as well.

## Build and run from source

From the repository root:

```powershell
dotnet restore MyMusic.csproj
dotnet build MyMusic.csproj -c Debug -p:Platform=x64
dotnet run --project MyMusic.csproj -c Debug -p:Platform=x64
```

## Build the V1 installer

Run:

```text
Build-V1-Installer.bat
```

The script publishes a self-contained `win-x64` build, verifies the WinUI resource index (`resources.pri`), and invokes Inno Setup 6.

The resulting installer is written to:

```text
Installer\Output\MyMusic-Setup-v1.0.0.exe
```

The generated `publish` directory and installer output are intentionally ignored by Git.

## User data

MyMusic stores its application data under:

```text
%APPDATA%\MyMusic
```

The default managed library is:

```text
%APPDATA%\MyMusic\Library
```

The library location can be changed from MyMusic settings. Uninstalling MyMusic can preserve this data for a later reinstall.

## Repository layout

```text
MyMusic/
├── .github/workflows/   GitHub Actions
├── Data/                SQLite/database code
├── Installer/           Inno Setup definition
├── Models/              Application models
├── Services/            Library/casting services
├── App.xaml             Application resources
├── MainWindow.xaml      Main application UI
├── MyMusic.csproj       .NET project
└── Build-V1-Installer.* Installer build scripts
```

## CI

GitHub Actions performs a Windows x64 Release build on pushes and pull requests to `main`. Installer creation remains a local release step for now.

## License

No open-source license has been selected yet. While this repository is private, all rights are reserved. Choose and add a license before making the source repository public if you intend to distribute it as open source.
