# VidFlow

A Windows 11 native (WinUI 3) downloader for YouTube and Bilibili, built on [yt-dlp](https://github.com/yt-dlp/yt-dlp).

## Features

- Single videos, playlists, Bilibili multi-part videos (pick parts), and batches of links
- Quality list with size estimates; MP4 / MP3 / M4A; subtitles and Bilibili danmaku
- Queue with pause / resume (continues partial files), speed limit, retry, copy error details
- Download list survives restarts; unfinished downloads resume automatically
- In-app sign-in (WebView2) for Bilibili 1080P+ and YouTube restricted videos
- Notifications, taskbar progress, tray icon (keeps downloading after closing), drag-and-drop, clipboard watching
- yt-dlp kept up to date automatically; ffmpeg installed on demand
- Chinese / English UI, light / dark theme, first-run setup wizard

## Layout

| Path | What |
|---|---|
| `VidFlow/` | WinUI 3 app (pages, windows, tray / notifications / taskbar integration) |
| `VidFlow.Core/` | UI-free logic: yt-dlp arguments and output parsing, info parsing, settings, persistence, localization |
| `VidFlow.Tests/` | Unit tests for `VidFlow.Core` (xunit v3) |
| `VidFlow.Core/Localization/Strings.tsv` | All UI strings (key, English, Chinese) — source of the `.resx` files |
| `installer/VidFlow.iss` | Inno Setup script (per-user install) |
| `tools/` | `gen-strings.ps1`, `build-release.ps1` |

## Build and test

Requires the .NET 10 SDK.

```powershell
dotnet build VidFlow/VidFlow.csproj -p:Platform=x64
dotnet test --project VidFlow.Tests/VidFlow.Tests.csproj
```

After editing `Strings.tsv`, run `pwsh tools/gen-strings.ps1` (the tests check that both languages have the same keys
and that every key used in code exists).

## Release

Requires [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`).

```powershell
pwsh tools/build-release.ps1
```

Runs the tests, publishes a trimmed self-contained x64 build to `VidFlow-Release/`, and writes the installer to
`dist/VidFlow-Setup-<version>-x64.exe`. Bump `<Version>` in `VidFlow/VidFlow.csproj` first.

## Data

Everything lives in `%LocalAppData%\VidFlow`: `settings.json`, `downloads.json`, `cookies.txt` (in-app sign-in),
`bin\` (yt-dlp, ffmpeg), `logs\`, `thumbs\` (thumbnail cache), `WebView2\` (sign-in session).
