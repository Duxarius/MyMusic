# MyMusic

MyMusic is a Windows desktop music player focused on a local, managed music library with a modern WinUI 3 interface.


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

## License

No open-source license has been selected yet. While this repository is private, all rights are reserved. Choose and add a license before making the source repository public if you intend to distribute it as open source.
