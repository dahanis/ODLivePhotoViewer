namespace OneDriveLivePhotoViewer.Services;

public sealed record FileTransferProgress(
    string Key,
    long BytesTransferred,
    long TotalBytes,
    string Label);
