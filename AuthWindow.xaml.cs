using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using OneDriveLivePhotoViewer.Services;

namespace OneDriveLivePhotoViewer;

public partial class AuthWindow : Window
{
    public static string AuthProfileFolder => Path.Combine(AppLogger.AppDataDirectory, "WebView2Profile");

    public static void ClearStoredAuthProfile()
    {
        try
        {
            if (Directory.Exists(AuthProfileFolder))
            {
                Directory.Delete(AuthProfileFolder, recursive: true);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log("Could not clear authentication WebView2 profile", ex);
        }
    }

    private readonly TaskCompletionSource<string> _tokenSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _eventHooked;
    private bool _accepted;

    public AuthWindow()
    {
        InitializeComponent();
        Loaded += AuthWindow_Loaded;
    }

    public string? AccessToken { get; private set; }
    public Task<string> TokenTask => _tokenSource.Task;

    private async void AuthWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            SetStatus("Loading secure sign-in...", "#F59E0B");
            var userDataFolder = AuthProfileFolder;
            Directory.CreateDirectory(userDataFolder);

            var options = new CoreWebView2EnvironmentOptions("--enable-features=msSingleSignOnOSForPrimaryAccountIsShared");
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: options);

            await Browser.EnsureCoreWebView2Async(environment);

            Browser.CoreWebView2.Settings.UserAgent =
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                "AppleWebKit/537.36 (KHTML, like Gecko) " +
                "Chrome/134.0.0.0 Safari/537.36 Edg/134.0.3124.85";

            Browser.CoreWebView2.WebResourceResponseReceived += CoreWebView2_WebResourceResponseReceived;
            _eventHooked = true;

            Browser.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess)
                {
                    AppLogger.Log($"Auth navigation failed. WebErrorStatus={args.WebErrorStatus}");
                    SetStatus($"Navigation issue: {args.WebErrorStatus}", "#DC2626");
                }
            };

            Browser.CoreWebView2.ProcessFailed += (_, args) =>
            {
                AppLogger.Log($"WebView2 process failed: {args.ProcessFailedKind}");
                SetStatus($"WebView2 process failed: {args.ProcessFailedKind}. Close and retry.", "#DC2626");
            };

            SetStatus("Sign in to OneDrive. Waiting for OneDrive Photos API request...", "#F59E0B");
            Browser.CoreWebView2.Navigate("https://onedrive.live.com/?view=8");
        }
        catch (Exception ex)
        {
            AppLogger.Log("Failed to initialize authentication WebView2", ex);
            SetStatus("Could not start WebView2 sign-in. See app.log for details.", "#DC2626");
            _tokenSource.TrySetException(ex);
        }
    }

    private void CoreWebView2_WebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        try
        {
            var uri = e.Request.Uri;
            var isOneDriveApi = uri.StartsWith("https://my.microsoftpersonalcontent.com/_api/", StringComparison.OrdinalIgnoreCase) ||
                                uri.StartsWith("https://api.onedrive.com/", StringComparison.OrdinalIgnoreCase);
            if (!isOneDriveApi) return;

            if (!e.Request.Headers.Contains("Authorization")) return;
            var token = e.Request.Headers.GetHeader("Authorization");
            if (string.IsNullOrWhiteSpace(token)) return;

            Dispatcher.InvokeAsync(() => CaptureToken(token));
        }
        catch (Exception ex)
        {
            AppLogger.Log("Failed while inspecting OneDrive API request", ex);
        }
    }

    private void CaptureToken(string token)
    {
        if (!string.IsNullOrWhiteSpace(AccessToken)) return;

        AccessToken = token;
        _tokenSource.TrySetResult(token);
        UseTokenButton.IsEnabled = true;
        SetStatus("Authentication captured. You can close this window or press “Use captured sign-in”.", "#15803D");

        // Important: do not close the WebView inside the network event. That was the fragile part of the first PoC.
        if (_eventHooked && Browser.CoreWebView2 is not null)
        {
            Browser.CoreWebView2.WebResourceResponseReceived -= CoreWebView2_WebResourceResponseReceived;
            _eventHooked = false;
        }
    }

    private void UseTokenButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(AccessToken))
        {
            _accepted = true;
            DialogResult = true;
            Close();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(AccessToken))
        {
            _accepted = true;
            DialogResult = true;
        }
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_accepted && !string.IsNullOrWhiteSpace(AccessToken))
        {
            _accepted = true;
            try
            {
                DialogResult = true;
            }
            catch (InvalidOperationException)
            {
                // Window was not opened modally; ignore.
            }
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            if (_eventHooked && Browser.CoreWebView2 is not null)
            {
                Browser.CoreWebView2.WebResourceResponseReceived -= CoreWebView2_WebResourceResponseReceived;
                _eventHooked = false;
            }
            Browser.Dispose();
        }
        catch (Exception ex)
        {
            AppLogger.Log("Error while closing auth window", ex);
        }

        if (!_accepted && string.IsNullOrWhiteSpace(AccessToken))
        {
            _tokenSource.TrySetCanceled();
        }
        base.OnClosed(e);
    }

    private void SetStatus(string text, string color)
    {
        StatusText.Text = text;
        StatusDot.Fill = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(color)!;
    }
}
