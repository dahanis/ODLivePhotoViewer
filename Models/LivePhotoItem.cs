using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace OneDriveLivePhotoViewer.Models;

public sealed class LivePhotoItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _movDownloaded;
    private string _status = "Found";
    private string? _downloadedMovPath;

    public string Name { get; init; } = string.Empty;
    public string ItemId { get; init; } = string.Empty;
    public string DriveId { get; init; } = string.Empty;
    public string CloudFolderPath { get; init; } = "\\";
    public string LocalFolderPath { get; init; } = string.Empty;
    public long ImageSize { get; init; }
    public long ExpectedVideoSize { get; init; }
    public DateTimeOffset TakenOrModified { get; init; }

    public string DisplayDate => TakenOrModified.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string ImagePath => Path.Combine(LocalFolderPath, Name);
    public string BaseName => Path.GetFileNameWithoutExtension(Name);
    public string PreferredMovPath => Path.Combine(LocalFolderPath, BaseName + ".mov");
    public string AlternateMovPath => Path.Combine(LocalFolderPath, BaseName + ".onedrive-live.mov");
    public string ExpectedVideoSizeText => ExpectedVideoSize > 0 ? FormatBytes(ExpectedVideoSize) : "unknown";
    public string CloudFullPath => CloudFolderPath.TrimEnd('\\') + "\\" + Name;
    public string SelectionText => IsSelected ? "Selected" : "";

    public string? ExistingMovPath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DownloadedMovPath) && File.Exists(DownloadedMovPath)) return DownloadedMovPath;
            if (IsExistingMovGood(PreferredMovPath, ExpectedVideoSize)) return PreferredMovPath;
            if (IsExistingMovGood(AlternateMovPath, ExpectedVideoSize)) return AlternateMovPath;
            return null;
        }
    }

    public string? DownloadedMovPath
    {
        get => _downloadedMovPath;
        set
        {
            if (_downloadedMovPath == value) return;
            _downloadedMovPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ExistingMovPath));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectionText));
        }
    }

    public bool MovDownloaded
    {
        get => _movDownloaded;
        set
        {
            if (_movDownloaded == value) return;
            _movDownloaded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ExistingMovPath));
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
        }
    }

    public void RefreshMovState()
    {
        var existing = ExistingMovPath;
        DownloadedMovPath = existing;
        MovDownloaded = existing is not null;
        if (MovDownloaded && (Status == "Found" || Status == "MOV missing")) Status = "MOV present";
    }

    public void MarkMovDownloaded(string path, string status = "MOV ready")
    {
        DownloadedMovPath = path;
        MovDownloaded = true;
        Status = status;
    }

    public override string ToString() => $"{Name} — {DisplayDate}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static bool IsExistingMovGood(string path, long expectedLength)
    {
        if (!File.Exists(path)) return false;
        var length = new FileInfo(path).Length;
        return expectedLength > 0 ? length == expectedLength : length > 64 * 1024;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "unknown";
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}
