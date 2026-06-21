# OneDrive Live Photo Viewer

A native Windows application for viewing and recovering iPhone Live Photos that were uploaded to OneDrive.

OneDrive preserves iPhone Live Photos, but the motion/video part is not normally exposed as a separate `.mov` file when you download the image on a PC. This app signs in to OneDrive, scans your OneDrive photo library, identifies Live Photos, downloads the hidden `.mov` counterpart, and lets you view the still image and motion clip together.

## What the app can do

### View OneDrive Live Photos on Windows

The app lets you browse Live Photos stored in OneDrive and preview them in a Windows interface.

When you select a Live Photo, the app shows the still image first. If the video part has already been downloaded, you can play the motion part by clicking or hovering over the photo, similar to the Live Photo behavior in Apple Photos.

### Download the hidden `.mov` part

For each detected Live Photo, the app can download the hidden video stream from OneDrive using the same OneDrive Photos API behavior used internally by Microsoft’s photo viewer.

The downloaded video is saved as a `.mov` file next to the still image, for example:

```text
IMG_1234.HEIC
IMG_1234.mov
```

If a conflicting `.mov` file already exists, the app avoids destructive overwrites and uses a safer fallback filename.

### Process a whole folder

You can choose a local OneDrive folder, such as:

```text
C:\Users\<you>\OneDrive\Pictures\Camera Roll
```

The app maps that local folder to its OneDrive cloud path and scans the corresponding OneDrive location for Live Photos.

This is useful when recovering the `.mov` files for many photos at once.

### Process one specific file

You can also select a single image file instead of scanning an entire folder.

This is useful when you only want to recover the `.mov` counterpart for one specific Live Photo.

Supported still-image inputs include typical iPhone photo formats such as:

```text
.heic
.heif
.jpg
.jpeg
.png
```

### Select which Live Photos to process

The library view includes checkboxes, allowing you to choose exactly which Live Photos should be downloaded or exported.

Selection tools include:

- Select all
- Clear selection
- Invert selection
- Download selected
- Export selected

This avoids unnecessary downloads when working with a large photo library.

### Download all Live Photos

The app also includes a bulk mode for downloading all detected `.mov` counterparts from the selected folder.

Bulk processing is optimized with throttled parallel downloads so that large libraries are processed faster without overwhelming the OneDrive service or the local machine.

### Export photo + video pairs

The app can create a new copy of selected or all Live Photos in another directory.

For each Live Photo, it copies both parts:

```text
still image + .mov video
```

This is useful if you want to create a clean archive, move your Live Photos somewhere else, or prepare a folder for another backup service.

### Show actual download progress

The bottom progress bar reflects download progress during bulk operations.

When OneDrive reports the expected video size, progress is calculated using actual downloaded bytes rather than only item count.

### Sign in and sign out

The app signs in through a WebView2 OneDrive window.

After sign-in, the app captures the OneDrive Photos authorization header needed to access the Live Photo video stream.

The app includes a sign-out option that clears the in-memory token and removes the app’s WebView2 authentication profile.

## How the app works

An iPhone Live Photo contains two parts:

```text
still image  -> HEIC/JPEG
motion clip  -> MOV
```

When uploaded through the OneDrive iPhone app, OneDrive may keep the Live Photo as one logical photo item. The still image is visible normally, but the video part is hidden from standard Windows download flows.

This app:

1. Opens a OneDrive sign-in window.
2. Captures the OneDrive Photos API authorization header.
3. Scans the selected OneDrive path for items where `photo.livePhoto` exists.
4. Downloads the normal image stream when needed.
5. Downloads the hidden video stream using OneDrive’s Live Photo video format endpoint.
6. Saves the `.mov` file locally.
7. Displays the still image first, then plays the motion clip on click or hover.

## Requirements

### Operating system

Windows 10 or Windows 11.

The project targets:

```text
net8.0-windows10.0.17763.0
```

### .NET SDK

To build from source, install the .NET 8 SDK.

Check your installed SDKs with:

```powershell
dotnet --list-sdks
```

If no SDK is listed, install the .NET 8 SDK first.

### WebView2 Runtime

Microsoft Edge WebView2 Runtime is required for sign-in and preview playback.

Most modern Windows installations already include it.

### HEIC / HEIF support

For `.heic` and `.heif` still images, Windows may require Microsoft’s HEIF Image Extensions.

If HEIC previews do not display, install the HEIF Image Extensions from the Microsoft Store.

### HEVC / MOV playback

For some iPhone MOV files, Windows may require HEVC video support.

If the video does not play, install the relevant HEVC codec support from Microsoft or another trusted codec provider.

## Build and run

From the project folder:

```powershell
dotnet restore
dotnet build -c Release
dotnet run -c Release
```

## Publish as a standalone Windows app

To create a standalone executable:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish\win-x64
```

The generated executable will be located in:

```text
.\publish\win-x64\OneDriveLivePhotoViewer.exe
```

## Basic usage

1. Start the app.
2. Click **Sign in**.
3. Complete OneDrive sign-in in the WebView2 window.
4. Choose either:
   - a OneDrive photos folder, or
   - one specific photo file.
5. Wait for the app to scan for Live Photos.
6. Select the Live Photos you want to process.
7. Use:
   - **Download / Make live** for the current photo,
   - **Download selected** for chosen photos,
   - **Download all** for every detected Live Photo,
   - **Export selected** or **Export all** to copy image + video pairs elsewhere.
8. Click or hover over the still image to play the Live Photo.

## Security notes

The app uses an authentication token from your OneDrive web session to access OneDrive Photos API endpoints.

The app is designed to keep this token in memory only. It is not intentionally written to disk.

Use **Sign out** when finished if you want to clear the local WebView2 session used by the app.

Because the app relies on private/undocumented OneDrive Photos behavior, Microsoft may change the underlying API at any time.

## Data safety

The app is designed to avoid destructive overwrites.

When downloading a `.mov` file, it checks whether an existing video already exists. If there is a conflict, it uses a safer fallback filename instead of blindly replacing unrelated files.

For exports, the app copies the still image and video pair into a new destination directory rather than moving the originals.

## Large library behavior

For large libraries, the app uses throttled parallel processing.

This means it can download multiple Live Photo videos at once, but with limits to avoid excessive load on the machine or OneDrive.

The progress bar updates during downloads and reflects actual downloaded bytes when file sizes are available.

## Logs

The app writes diagnostic logs here:

```text
%LOCALAPPDATA%\OneDriveLivePhotoViewer\app.log
```

Use the log file for troubleshooting sign-in, scanning, download, codec, or playback issues.

## Known limitations

### OneDrive API behavior may change

The app relies on OneDrive Photos behavior that is not part of the public Microsoft Graph API for Live Photo video downloads. If Microsoft changes this behavior, downloading hidden `.mov` streams may stop working until the app is updated.

### HEIC support depends on Windows codecs

The app can prepare a preview cache for HEIC files, but Windows still needs the correct image extensions/codecs to decode some files.

### MOV playback depends on installed codecs

Some iPhone video clips may require HEVC support on Windows.

### OneDrive sync state matters

The local still image must be available or accessible from the selected OneDrive path. If the image is cloud-only and not hydrated locally, Windows or OneDrive may need to download it first.

### Shared folders may behave differently

Shared OneDrive folders can have different drive IDs and permissions. The app attempts to handle shared folders, but behavior may differ depending on how the folder was shared.

## Troubleshooting

### The app signs in but finds no Live Photos

Check that the selected local folder corresponds to the same OneDrive account used during sign-in.

Also confirm that the photos were uploaded as Live Photos from the OneDrive iPhone app.

### The still image does not show

For `.heic` files, install HEIF Image Extensions from Microsoft.

Also check whether the file is fully available locally and not only a cloud placeholder.

### The video does not play

Install HEVC video support if the MOV uses HEVC.

Also confirm that the `.mov` file was downloaded successfully and has a nonzero file size.

### Downloads fail randomly

OneDrive may occasionally return temporary service errors. Retry the operation. Already downloaded valid files are skipped where possible.

### PowerShell blocks scripts

You do not need to run PowerShell scripts. You can use the `dotnet` commands directly.

## Project status

This app is intended as a practical Windows utility for recovering and viewing OneDrive-hosted iPhone Live Photos.

It is not an official Microsoft application.

Use it with care, keep backups of important photos, and verify exported files before deleting anything from OneDrive or your iPhone.
