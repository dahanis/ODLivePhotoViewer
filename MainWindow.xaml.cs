using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using OneDriveLivePhotoViewer.Models;
using OneDriveLivePhotoViewer.Services;
using Forms = System.Windows.Forms;

namespace OneDriveLivePhotoViewer;

public partial class MainWindow : Window
{
    private const int BulkParallelism = 4;

    private OneDrivePathMapping? _mapping;
    private OneDriveLivePhotoApi? _api;
    private CancellationTokenSource? _workCts;
    private Task? _viewerInitializationTask;
    private bool _isBusy;

    public ObservableCollection<LivePhotoItem> Items { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += MainWindow_Loaded;
        Closed += (_, _) =>
        {
            _workCts?.Cancel();
            _workCts?.Dispose();
            _api?.Dispose();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeViewerAsync();
        ShowWelcomePage();
        UpdateCommandState();
    }

    private Task InitializeViewerAsync()
    {
        if (Viewer.CoreWebView2 is not null) return Task.CompletedTask;
        _viewerInitializationTask ??= InitializeViewerCoreAsync();
        return _viewerInitializationTask;
    }

    private async Task InitializeViewerCoreAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(AppLogger.AppDataDirectory, "ViewerWebView2Profile");
            Directory.CreateDirectory(userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await Viewer.EnsureCoreWebView2Async(environment);
            Viewer.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        }
        catch (Exception ex)
        {
            AppLogger.Log("Failed to initialize viewer WebView2", ex);
            SetStatus("Viewer could not start. Install/update Microsoft Edge WebView2 Runtime.");
            _viewerInitializationTask = null;
        }
    }

    private void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose a folder inside your local OneDrive, for example OneDrive\\Pictures\\Camera Roll",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        var result = dialog.ShowDialog();
        if (result != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;

        var mapping = LocalOneDriveMapper.TryMapLocalFolder(dialog.SelectedPath);
        if (mapping is null)
        {
            System.Windows.MessageBox.Show(
                this,
                "This folder does not appear to be under a local OneDrive sync root. Choose a folder under OneDrive, for example Pictures\\Camera Roll.",
                "Not a OneDrive folder",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        ApplyMapping(mapping, "Folder mapped to OneDrive cloud path: ");
    }

    private void ChooseFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose one local OneDrive photo",
            Filter = "Photo files|*.heic;*.heif;*.jpg;*.jpeg;*.png|All files|*.*",
            Multiselect = false,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FileName)) return;

        var mapping = LocalOneDriveMapper.TryMapLocalFile(dialog.FileName);
        if (mapping is null)
        {
            System.Windows.MessageBox.Show(
                this,
                "This file does not appear to be under a local OneDrive sync root. Choose a photo inside your local OneDrive folder.",
                "Not a OneDrive file",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        ApplyMapping(mapping, "Single file mapped to OneDrive cloud folder: ");
    }

    private void ApplyMapping(OneDrivePathMapping mapping, string statusPrefix)
    {
        _mapping = mapping;
        LocalPathBox.Text = mapping.DisplayPath;
        CloudPathText.Text = mapping.IsSpecificFile
            ? "Cloud file: " + mapping.CloudPath + mapping.SpecificFileName
            : "Cloud path: " + mapping.CloudPath;
        SetStatus(statusPrefix + mapping.CloudPath + (mapping.SpecificFileName ?? string.Empty));
        UpdateCommandState();
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        await SignInAsync();
    }

    private async Task SignInAsync()
    {
        try
        {
            SetBusy(true, "Opening OneDrive sign-in...");
            var auth = new AuthWindow { Owner = this };
            var result = auth.ShowDialog();
            if (result != true || string.IsNullOrWhiteSpace(auth.AccessToken))
            {
                SetStatus("Sign-in cancelled or no token was captured.");
                return;
            }

            _api?.Dispose();
            _api = new OneDriveLivePhotoApi(auth.AccessToken);

            SetStatus("Validating OneDrive Photos token...");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _api.ValidateAsync(cts.Token);

            AuthStatusText.Text = "Signed in";
            SignInButton.Content = "Signed in";
            SetStatus("Signed in. Token is stored in memory only.");
            UpdateCommandState();
        }
        catch (Exception ex)
        {
            _api?.Dispose();
            _api = null;
            AppLogger.Log("Sign-in failed", ex);
            System.Windows.MessageBox.Show(this, ex.Message, "Sign-in failed", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("Sign-in failed. See app.log for details.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SignOutButton_Click(object sender, RoutedEventArgs e)
    {
        _workCts?.Cancel();
        _api?.Dispose();
        _api = null;
        AuthWindow.ClearStoredAuthProfile();
        AuthStatusText.Text = "Signed out";
        SignInButton.Content = "Sign in";
        SetStatus("Signed out. In-memory token cleared and the app's WebView sign-in profile was removed.");
        UpdateCommandState();
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        await ScanAsync();
    }

    private async Task ScanAsync()
    {
        if (_api is null || _mapping is null) return;

        foreach (var oldItem in Items)
        {
            oldItem.PropertyChanged -= LivePhotoItem_PropertyChanged;
        }
        Items.Clear();
        CountText.Text = "0 Live Photos";
        SelectionText.Text = "No selected items";
        ShowWelcomePage();
        ResetWorkCancellation();

        try
        {
            SetBusy(true, _mapping.IsSpecificFile ? "Scanning OneDrive for the selected file..." : "Scanning OneDrive for Live Photos...");
            Progress.IsIndeterminate = true;
            var count = 0;
            var scanned = 0;
            await foreach (var item in _api.ScanAsync(_mapping.SelectedLocalPath, _mapping.CloudPath, _workCts!.Token))
            {
                scanned++;
                if (_mapping.IsSpecificFile && !item.Name.Equals(_mapping.SpecificFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                item.PropertyChanged += LivePhotoItem_PropertyChanged;
                Items.Add(item);
                count++;
                CountText.Text = count == 1 ? "1 Live Photo" : $"{count} Live Photos";
                if (count % 25 == 0) SetStatus($"Found {count} Live Photos so far...");
            }

            SetStatus(count == 0
                ? (_mapping.IsSpecificFile
                    ? "Scan complete. The selected file was not found as a OneDrive Live Photo."
                    : "Scan complete. No Live Photos were found in that cloud path.")
                : (_mapping.IsSpecificFile
                    ? $"Scan complete. The selected file is a Live Photo."
                    : $"Scan complete. Found {count} Live Photos."));
        }
        catch (OperationCanceledException)
        {
            SetStatus("Scan cancelled.");
        }
        catch (Exception ex)
        {
            AppLogger.Log("Scan failed", ex);
            System.Windows.MessageBox.Show(this, ex.Message, "Scan failed", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("Scan failed. See app.log for details.");
        }
        finally
        {
            Progress.IsIndeterminate = false;
            SetBusy(false);
            UpdateSelectionText();
            UpdateCommandState();
        }
    }

    private void LivePhotoItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LivePhotoItem.IsSelected))
        {
            Dispatcher.InvokeAsync(() =>
            {
                UpdateSelectionText();
                UpdateCommandState();
            });
        }
    }

    private async void DownloadSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        await DownloadItemsAsync(GetSelectedItems(), "selected");
    }

    private async void DownloadAllButton_Click(object sender, RoutedEventArgs e)
    {
        await DownloadItemsAsync(Items.ToList(), "all");
    }

    private async Task DownloadItemsAsync(IReadOnlyList<LivePhotoItem> targetItems, string label)
    {
        if (_api is null || targetItems.Count == 0) return;

        ResetWorkCancellation();
        SetBusy(true, $"Downloading MOV sidecars for {targetItems.Count} {label} item(s)...");
        Progress.IsIndeterminate = false;
        Progress.Value = 0;

        var totalExpectedBytes = targetItems.Where(item => item.ExpectedVideoSize > 0).Sum(item => item.ExpectedVideoSize);
        var transferProgress = CreateTransferProgress(totalExpectedBytes, "Downloading MOV sidecars");

        var completed = 0;
        var errors = new ConcurrentBag<string>();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = BulkParallelism,
            CancellationToken = _workCts!.Token
        };

        try
        {
            await Parallel.ForEachAsync(targetItems, options, async (item, token) =>
            {
                try
                {
                    await Dispatcher.InvokeAsync(() => item.Status = "Downloading...");
                    var movPath = await _api.DownloadMovAsync(item, token, transferProgress, item.ItemId);
                    var currentCompleted = Interlocked.Increment(ref completed);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        item.MarkMovDownloaded(movPath, "MOV ready");
                        if (transferProgress is null)
                        {
                            UpdateBulkProgress(currentCompleted, targetItems.Count, $"Downloaded {currentCompleted}/{targetItems.Count}: {item.Name}");
                        }
                        else
                        {
                            SetProgressTextOnly($"Downloaded {currentCompleted}/{targetItems.Count}: {item.Name}");
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add($"{item.Name}: {ex.Message}");
                    var currentCompleted = Interlocked.Increment(ref completed);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        item.Status = "Failed";
                        if (transferProgress is null)
                        {
                            UpdateBulkProgress(currentCompleted, targetItems.Count, $"Processed {currentCompleted}/{targetItems.Count}; one item failed.");
                        }
                        else
                        {
                            SetProgressTextOnly($"Processed {currentCompleted}/{targetItems.Count}; one item failed.");
                        }
                    });
                    AppLogger.Log($"Failed to download MOV for {item.Name}", ex);
                }
            });

            if (transferProgress is not null) Progress.Value = 100;
            FinishBulkOperation("Download", targetItems.Count, errors);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Download cancelled.");
        }
        finally
        {
            SetBusy(false);
            UpdateCommandState();
        }
    }

    private async void ExportSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        await ExportItemsAsync(GetSelectedItems(), "selected");
    }

    private async void ExportAllButton_Click(object sender, RoutedEventArgs e)
    {
        await ExportItemsAsync(Items.ToList(), "all");
    }

    private async Task ExportItemsAsync(IReadOnlyList<LivePhotoItem> targetItems, string label)
    {
        if (_api is null || _mapping is null || targetItems.Count == 0) return;

        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose an export folder for copied still images and MOV sidecars",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;

        var destinationRoot = dialog.SelectedPath;
        ResetWorkCancellation();
        SetBusy(true, $"Exporting {targetItems.Count} {label} Live Photo pair(s)...");
        Progress.IsIndeterminate = false;
        Progress.Value = 0;

        var totalExpectedBytes = targetItems.Where(item => item.ExpectedVideoSize > 0).Sum(item => item.ExpectedVideoSize);
        var transferProgress = CreateTransferProgress(totalExpectedBytes, "Downloading MOVs for export");

        var completed = 0;
        var errors = new ConcurrentBag<string>();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = BulkParallelism,
            CancellationToken = _workCts!.Token
        };

        try
        {
            await Parallel.ForEachAsync(targetItems, options, async (item, token) =>
            {
                try
                {
                    await Dispatcher.InvokeAsync(() => item.Status = "Exporting...");
                    var movPath = await _api.DownloadMovAsync(item, token, transferProgress, item.ItemId);
                    var exportFolder = GetExportFolder(destinationRoot, item);
                    Directory.CreateDirectory(exportFolder);

                    var imageTarget = BuildSafeTargetPath(exportFolder, item.Name, item.ImageSize, unknownLengthMinimum: 1);
                    if (File.Exists(item.ImagePath))
                    {
                        await CopyFileAsync(item.ImagePath, imageTarget, token);
                    }
                    else
                    {
                        await _api.DownloadImageAsync(item, imageTarget, token);
                    }

                    var movTarget = BuildSafeTargetPath(exportFolder, item.BaseName + ".mov", item.ExpectedVideoSize, unknownLengthMinimum: 64 * 1024);
                    await CopyFileAsync(movPath, movTarget, token);

                    var currentCompleted = Interlocked.Increment(ref completed);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        item.MarkMovDownloaded(movPath, "Exported");
                        if (transferProgress is null)
                        {
                            UpdateBulkProgress(currentCompleted, targetItems.Count, $"Exported {currentCompleted}/{targetItems.Count}: {item.Name}");
                        }
                        else
                        {
                            SetProgressTextOnly($"Exported {currentCompleted}/{targetItems.Count}: {item.Name}");
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add($"{item.Name}: {ex.Message}");
                    var currentCompleted = Interlocked.Increment(ref completed);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        item.Status = "Export failed";
                        if (transferProgress is null)
                        {
                            UpdateBulkProgress(currentCompleted, targetItems.Count, $"Processed {currentCompleted}/{targetItems.Count}; one export failed.");
                        }
                        else
                        {
                            SetProgressTextOnly($"Processed {currentCompleted}/{targetItems.Count}; one export failed.");
                        }
                    });
                    AppLogger.Log($"Failed to export {item.Name}", ex);
                }
            });

            if (transferProgress is not null) Progress.Value = 100;
            FinishBulkOperation("Export", targetItems.Count, errors);

            if (errors.IsEmpty)
            {
                var open = System.Windows.MessageBox.Show(this, "Export complete. Open the export folder?", "Export complete", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (open == MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{destinationRoot}\"") { UseShellExecute = true });
                }
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("Export cancelled.");
        }
        finally
        {
            SetBusy(false);
            UpdateCommandState();
        }
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        await DownloadAndPlaySelectedAsync();
    }

    private async void LivePhotoList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        await DownloadAndPlaySelectedAsync();
    }

    private async Task DownloadAndPlaySelectedAsync()
    {
        if (_api is null || LivePhotoList.SelectedItem is not LivePhotoItem item) return;

        ResetWorkCancellation();

        try
        {
            SetBusy(true, "Preparing Live Photo...");
            Progress.IsIndeterminate = false;
            Progress.Value = 0;
            item.Status = "Downloading...";

            var expectedTotal = 0L;
            if (!File.Exists(item.ImagePath) && item.ImageSize > 0) expectedTotal += item.ImageSize;
            if (item.ExpectedVideoSize > 0) expectedTotal += item.ExpectedVideoSize;
            var transferProgress = CreateTransferProgress(expectedTotal, "Preparing Live Photo");

            if (!File.Exists(item.ImagePath))
            {
                await _api.DownloadImageAsync(item, item.ImagePath, _workCts!.Token, transferProgress, item.ItemId + ":image");
            }

            var movPath = await _api.DownloadMovAsync(item, _workCts!.Token, transferProgress, item.ItemId + ":mov");
            item.MarkMovDownloaded(movPath, "MOV ready");
            if (transferProgress is not null) Progress.Value = 100;
            await ShowLivePhotoAsync(item, movPath);
            SetStatus("Ready: click or hover the still image to play " + item.Name);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Playback preparation cancelled.");
        }
        catch (Exception ex)
        {
            AppLogger.Log("Download/play selected failed", ex);
            System.Windows.MessageBox.Show(this, ex.Message, "Could not play Live Photo", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("Could not play selected Live Photo. See app.log for details.");
        }
        finally
        {
            Progress.IsIndeterminate = false;
            SetBusy(false);
            UpdateCommandState();
        }
    }

    private async void LivePhotoList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateCommandState();
        if (LivePhotoList.SelectedItem is not LivePhotoItem item)
        {
            ViewerTitle.Text = "Viewer";
            ViewerSubtitle.Text = "Select a photo. HEIC stills are converted to a temporary JPEG preview; click or hover to play the Live Photo.";
            return;
        }

        ViewerTitle.Text = item.Name;
        ViewerSubtitle.Text = item.CloudFullPath;
        await ShowSelectedItemAsync(item);
    }

    private async Task ShowSelectedItemAsync(LivePhotoItem item)
    {
        item.RefreshMovState();
        var movPath = item.ExistingMovPath;
        if (!string.IsNullOrWhiteSpace(movPath) && File.Exists(item.ImagePath))
        {
            await ShowLivePhotoAsync(item, movPath);
            SetStatus($"Selected {item.Name}. Click or hover the still image to play it.");
            return;
        }

        await ShowStillPreviewAsync(item);
    }

    private async Task ShowStillPreviewAsync(LivePhotoItem item)
    {
        await InitializeViewerAsync();
        if (!File.Exists(item.ImagePath))
        {
            Viewer.NavigateToString(HtmlShell($"""
                <div class='empty'>
                  <h1>Still image not available locally</h1>
                  <p>OneDrive may not have downloaded <strong>{WebUtility.HtmlEncode(item.Name)}</strong> to this PC yet. Press <strong>Download / Make live</strong> and the app will fetch both parts from OneDrive.</p>
                </div>
                """));
            return;
        }

        string previewPath;
        try
        {
            previewPath = await StillPreviewCache.GetPreviewImageAsync(item);
        }
        catch (Exception ex)
        {
            AppLogger.Log("Still preview conversion failed", ex);
            Viewer.NavigateToString(HtmlShell($"""
                <div class='empty'>
                  <h1>Still image preview unavailable</h1>
                  <p><strong>{WebUtility.HtmlEncode(item.Name)}</strong> appears to be HEIC/HEIF or another format WebView2 cannot show directly. Install Microsoft's HEIF Image Extensions and HEVC Video Extensions, then reopen the app. The MOV download/export can still work.</p>
                  <p>{WebUtility.HtmlEncode(ex.Message)}</p>
                </div>
                """));
            return;
        }

        await ConfigureViewerFolderMappingAsync();
        var imageUri = ToViewerUrl(previewPath);
        var hint = item.MovDownloaded
            ? "MOV detected. Click or hover the still image to play the Live Photo."
            : "Still image shown. Press “Download / Make live” to fetch the hidden MOV sidecar.";

        Viewer.NavigateToString(HtmlShell($"""
            <div class='stage'>
              <img class='photo still-only' src='{imageUri}' alt='Live Photo still' />
              <div class='hint'>{WebUtility.HtmlEncode(hint)}</div>
            </div>
            """));
    }

    private async Task ShowLivePhotoAsync(LivePhotoItem item, string movPath)
    {
        await InitializeViewerAsync();

        if (!File.Exists(item.ImagePath))
        {
            throw new FileNotFoundException("The still image is not present locally.", item.ImagePath);
        }
        if (!File.Exists(movPath))
        {
            throw new FileNotFoundException("The MOV sidecar was not downloaded.", movPath);
        }

        string previewPath;
        try
        {
            previewPath = await StillPreviewCache.GetPreviewImageAsync(item);
        }
        catch (Exception ex)
        {
            AppLogger.Log("Still preview conversion failed", ex);
            throw new InvalidOperationException("The MOV sidecar was downloaded, but the still image cannot be previewed. Install Microsoft's HEIF Image Extensions and HEVC Video Extensions for HEIC support.", ex);
        }

        await ConfigureViewerFolderMappingAsync();
        var imageUri = ToViewerUrl(previewPath);
        var movUri = ToViewerUrl(movPath);
        var title = WebUtility.HtmlEncode(item.Name);

        Viewer.NavigateToString(HtmlShell($$"""
            <div class='stage live' id='stage'>
              <img id='still' class='photo' src='{{imageUri}}' alt='{{title}}' />
              <video id='video' class='video' src='{{movUri}}' muted playsinline preload='metadata'></video>
              <button id='play' class='play' title='Play Live Photo'>▶</button>
              <div class='hint'>Still image first. Click or hover to play the Live Photo.</div>
            </div>
            <script>
              const stage = document.getElementById('stage');
              const video = document.getElementById('video');
              const still = document.getElementById('still');
              const play = document.getElementById('play');
              let playing = false;
              async function start() {
                if (playing) return;
                playing = true;
                try {
                  video.currentTime = 0;
                  video.style.opacity = 1;
                  still.style.opacity = 0;
                  play.style.opacity = 0;
                  await video.play();
                } catch(e) { console.log(e); playing = false; }
              }
              function stop() {
                video.pause();
                video.currentTime = 0;
                video.style.opacity = 0;
                still.style.opacity = 1;
                play.style.opacity = 1;
                playing = false;
              }
              video.addEventListener('ended', stop);
              stage.addEventListener('mouseenter', start);
              stage.addEventListener('mouseleave', stop);
              stage.addEventListener('click', start);
            </script>
            """));
    }

    private static string HtmlShell(string body)
    {
        return $$"""
<!doctype html>
<html>
<head>
<meta charset='utf-8'>
<meta http-equiv='Content-Security-Policy' content="default-src 'self' file: data: https://livephoto.local https://livephoto-cache.local; img-src file: data: https://livephoto.local https://livephoto-cache.local; media-src file: data: https://livephoto.local https://livephoto-cache.local; style-src 'unsafe-inline'; script-src 'unsafe-inline';">
<style>
html, body { margin:0; width:100%; height:100%; overflow:hidden; background:#111827; color:#f9fafb; font-family:'Segoe UI', system-ui, sans-serif; }
.stage { position:relative; width:100vw; height:100vh; display:flex; align-items:center; justify-content:center; background:radial-gradient(circle at top, #1f2937, #030712); }
.photo, .video { position:absolute; max-width:100%; max-height:100%; object-fit:contain; transition:opacity 180ms ease; }
.still-only { opacity:1; }
.video { opacity:0; }
.live { cursor:pointer; }
.play { position:absolute; width:72px; height:72px; border:0; border-radius:50%; background:rgba(255,255,255,.88); color:#111827; font-size:28px; line-height:72px; text-align:center; box-shadow:0 18px 50px rgba(0,0,0,.35); cursor:pointer; transition:transform 120ms ease, opacity 180ms ease; }
.play:hover { transform:scale(1.06); }
.hint { position:absolute; left:50%; bottom:24px; transform:translateX(-50%); padding:10px 14px; border-radius:999px; color:#e5e7eb; background:rgba(17,24,39,.66); backdrop-filter:blur(10px); font-size:13px; white-space:nowrap; max-width:92%; overflow:hidden; text-overflow:ellipsis; }
.empty { height:100%; display:flex; flex-direction:column; align-items:center; justify-content:center; text-align:center; padding:42px; box-sizing:border-box; }
.empty h1 { font-size:26px; margin:0 0 10px; }
.empty p { color:#cbd5e1; max-width:680px; line-height:1.55; }
</style>
</head>
<body>
{{body}}
</body>
</html>
""";
    }

    private void ShowWelcomePage()
    {
        if (Viewer.CoreWebView2 is null) return;
        Viewer.NavigateToString(HtmlShell("""
            <div class='empty'>
              <h1>Choose a OneDrive folder or one photo file and sign in</h1>
              <p>The app scans OneDrive for items marked as Live Photos. You can process a whole folder or only one selected .HEIC/.JPG file, download selected MOV sidecars, and export still+MOV pairs to a separate folder.</p>
            </div>
            """));
    }

    private async Task ConfigureViewerFolderMappingAsync()
    {
        await InitializeViewerAsync();
        if (_mapping is null || Viewer.CoreWebView2 is null) return;

        try
        {
            Viewer.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "livephoto.local",
                _mapping.SelectedLocalPath,
                CoreWebView2HostResourceAccessKind.Allow);

            Directory.CreateDirectory(StillPreviewCache.CacheDirectory);
            Viewer.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "livephoto-cache.local",
                StillPreviewCache.CacheDirectory,
                CoreWebView2HostResourceAccessKind.Allow);
        }
        catch (Exception ex)
        {
            AppLogger.Log("Could not configure WebView2 folder mapping", ex);
        }
    }

    private string ToViewerUrl(string localPath)
    {
        if (_mapping is not null && IsSameOrChild(localPath, _mapping.SelectedLocalPath))
        {
            return ToVirtualHostUrl("livephoto.local", _mapping.SelectedLocalPath, localPath);
        }

        if (IsSameOrChild(localPath, StillPreviewCache.CacheDirectory))
        {
            return ToVirtualHostUrl("livephoto-cache.local", StillPreviewCache.CacheDirectory, localPath);
        }

        return new Uri(localPath).AbsoluteUri;
    }

    private static string ToVirtualHostUrl(string host, string root, string localPath)
    {
        var relative = Path.GetRelativePath(root, localPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

        var encoded = string.Join("/", relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return "https://" + host + "/" + encoded;
    }

    private static bool IsSameOrChild(string path, string root)
    {
        var fullPath = EnsureTrailingSlash(Path.GetFullPath(path));
        var fullRoot = EnsureTrailingSlash(Path.GetFullPath(root));
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSlash(string path)
        => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (LivePhotoList.SelectedItem is not LivePhotoItem item) return;
        try
        {
            item.RefreshMovState();
            var pathToSelect = item.ExistingMovPath is { } movPath ? movPath : item.ImagePath;
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{pathToSelect}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Log("Could not open folder", ex);
            SetStatus("Could not open File Explorer.");
        }
    }

    private void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppLogger.AppDataDirectory);
            if (!File.Exists(AppLogger.LogFilePath)) File.WriteAllText(AppLogger.LogFilePath, "");
            Process.Start(new ProcessStartInfo("notepad.exe", AppLogger.LogFilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Log("Could not open log", ex);
        }
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Items) item.IsSelected = true;
        UpdateSelectionText();
        UpdateCommandState();
    }

    private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Items) item.IsSelected = false;
        UpdateSelectionText();
        UpdateCommandState();
    }

    private void InvertSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Items) item.IsSelected = !item.IsSelected;
        UpdateSelectionText();
        UpdateCommandState();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _workCts?.Cancel();
        SetStatus("Cancelling...");
    }

    private void ResetWorkCancellation()
    {
        _workCts?.Cancel();
        _workCts?.Dispose();
        _workCts = new CancellationTokenSource();
    }

    private IReadOnlyList<LivePhotoItem> GetSelectedItems()
        => Items.Where(item => item.IsSelected).ToList();

    private void UpdateSelectionText()
    {
        var selected = Items.Count(item => item.IsSelected);
        SelectionText.Text = selected == 0 ? "No selected items" : selected == 1 ? "1 selected" : $"{selected} selected";
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _isBusy = busy;
        UpdateCommandState();
        CancelButton.IsEnabled = busy;
        if (status is not null) SetStatus(status);
        if (!busy)
        {
            Progress.IsIndeterminate = false;
        }
    }

    private void UpdateCommandState()
    {
        var signedIn = _api is not null;
        var hasMapping = _mapping is not null;
        var hasItems = Items.Count > 0;
        var hasSelected = Items.Any(item => item.IsSelected);
        var hasListSelection = LivePhotoList.SelectedItem is LivePhotoItem;

        ScanButton.IsEnabled = !_isBusy && signedIn && hasMapping;
        DownloadSelectedButton.IsEnabled = !_isBusy && signedIn && hasSelected;
        DownloadAllButton.IsEnabled = !_isBusy && signedIn && hasItems;
        ExportSelectedButton.IsEnabled = !_isBusy && signedIn && hasSelected;
        ExportAllButton.IsEnabled = !_isBusy && signedIn && hasItems;
        PlayButton.IsEnabled = !_isBusy && signedIn && hasListSelection;
        OpenFolderButton.IsEnabled = !_isBusy && hasListSelection;
        SelectAllButton.IsEnabled = !_isBusy && hasItems;
        ClearSelectionButton.IsEnabled = !_isBusy && hasSelected;
        InvertSelectionButton.IsEnabled = !_isBusy && hasItems;
        SignInButton.IsEnabled = !_isBusy;
        SignOutButton.IsEnabled = !_isBusy && signedIn;
    }

    private IProgress<FileTransferProgress>? CreateTransferProgress(long expectedTotalBytes, string operationName)
    {
        if (expectedTotalBytes <= 0) return null;

        var bytesByKey = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var lastUiUpdate = DateTime.MinValue;

        return new Progress<FileTransferProgress>(progress =>
        {
            var completedForKey = progress.TotalBytes > 0
                ? Math.Min(progress.BytesTransferred, progress.TotalBytes)
                : progress.BytesTransferred;
            bytesByKey[progress.Key] = Math.Max(0, completedForKey);

            var completedBytes = Math.Min(expectedTotalBytes, bytesByKey.Values.Sum());
            var now = DateTime.UtcNow;
            if ((now - lastUiUpdate).TotalMilliseconds < 90 && completedBytes < expectedTotalBytes)
            {
                return;
            }

            lastUiUpdate = now;
            Progress.Value = completedBytes * 100.0 / expectedTotalBytes;
            StatusBarText.Text = $"{operationName}: {LivePhotoItem.FormatBytes(completedBytes)} / {LivePhotoItem.FormatBytes(expectedTotalBytes)}";
        });
    }

    private void SetProgressTextOnly(string text)
    {
        StatusBarText.Text = text;
    }

    private void UpdateBulkProgress(int completed, int total, string status)
    {
        if (total <= 0) return;
        Progress.Value = completed * 100.0 / total;
        SetStatus(status);
    }

    private void FinishBulkOperation(string operationName, int total, ConcurrentBag<string> errors)
    {
        if (errors.IsEmpty)
        {
            SetStatus($"{operationName} complete. Processed {total} item(s).");
            return;
        }

        var details = string.Join(Environment.NewLine, errors.Take(8));
        if (errors.Count > 8) details += Environment.NewLine + $"...and {errors.Count - 8} more.";
        AppLogger.Log($"{operationName} completed with {errors.Count} error(s):{Environment.NewLine}{details}");
        System.Windows.MessageBox.Show(
            this,
            $"{operationName} completed with {errors.Count} error(s). First errors:{Environment.NewLine}{details}",
            operationName + " completed with errors",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        SetStatus($"{operationName} complete with {errors.Count} error(s). See app.log for details.");
    }

    private string GetExportFolder(string destinationRoot, LivePhotoItem item)
    {
        if (_mapping is null) return destinationRoot;
        var relative = Path.GetRelativePath(_mapping.SelectedLocalPath, item.LocalFolderPath);
        if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith("..")) return destinationRoot;
        return Path.Combine(destinationRoot, relative);
    }

    private static string BuildSafeTargetPath(string folder, string fileName, long expectedLength, long unknownLengthMinimum)
    {
        var target = Path.Combine(folder, fileName);
        if (!File.Exists(target)) return target;

        var currentLength = new FileInfo(target).Length;
        var sameFile = expectedLength > 0
            ? currentLength == expectedLength
            : currentLength >= unknownLengthMinimum;
        if (sameFile) return target;

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var i = 1; i < 10_000; i++)
        {
            var candidate = Path.Combine(folder, $"{baseName} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(folder, $"{baseName} ({Guid.NewGuid():N}){extension}");
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        if (File.Exists(destination) && new FileInfo(destination).Length == new FileInfo(source).Length)
        {
            return;
        }

        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".copy";
        try
        {
            await using (var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destinationStream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await sourceStream.CopyToAsync(destinationStream, cancellationToken);
            }

            File.SetLastWriteTimeUtc(temp, File.GetLastWriteTimeUtc(source));
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(temp, destination);
        }
        catch
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
                // best effort cleanup
            }
            throw;
        }
    }

    private void SetStatus(string text)
    {
        StatusBarText.Text = text;
        AppLogger.Log(text);
    }
}
