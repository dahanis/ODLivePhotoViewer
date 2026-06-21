# OneDrive Live Photo Viewer v2.6

Windows WPF app for recovering and viewing iPhone Live Photos uploaded by the OneDrive iOS app.

The app uses the same private OneDrive Photos behavior as the original PowerShell script: it signs in through OneDrive Photos, captures the OneDrive Photos `Authorization` header in memory, scans for `photo.livePhoto`, and downloads the hidden video stream using `content?format=video`.

## What's new in v2.6

- Choose a whole OneDrive folder **or one specific photo file** to process.
- HEIC/HEIF stills are converted to a temporary JPEG preview so WebView2 can display the still image before playback.
- Clicking a library item now shows the still photo first.
- If the MOV exists or is downloaded, click/hover the still photo to play the Live Photo.
- Select exactly which photos should get their MOV sidecars downloaded.
- Export selected/all Live Photo pairs into a new directory as still image + `.mov`.
- Sign out clears the in-memory token and the app's WebView2 auth profile.
- Bulk download uses throttled parallelism and byte-level download progress when OneDrive reports expected MOV sizes.

## Build and run

```powershell
dotnet restore
dotnet build -c Release
dotnet run -c Release
```

## Publish standalone EXE

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish\win-x64
```

Or run:

```powershell
.\publish-win-x64.cmd
```

## HEIC / HEVC requirements

WebView2 does not reliably display `.heic` images directly. v2.6 converts HEIC/HEIF stills to cached JPEG previews using Windows Imaging Component.

If the still preview fails, install Microsoft's HEIF Image Extensions and HEVC Video Extensions, then reopen the app. MOV playback may also require the HEVC codec depending on how the iPhone encoded the Live Photo.

## Logs

Logs are written to:

```text
%LOCALAPPDATA%\OneDriveLivePhotoViewer\app.log
```

## Security notes

- The OneDrive auth token is kept in memory only.
- It is not written to disk by this app.
- The app uses undocumented/private OneDrive Photos endpoints, so Microsoft may change behavior at any time.
