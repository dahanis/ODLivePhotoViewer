using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OneDriveLivePhotoViewer.Models;

namespace OneDriveLivePhotoViewer.Services;

public sealed class OneDriveLivePhotoApi : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _accessToken;

    public OneDriveLivePhotoApi(string accessToken)
    {
        _accessToken = accessToken;
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 8,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(8)
        };
    }

    public async Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendGetAsync(BuildChildrenUri(elementId: string.Empty, driveId: string.Empty), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(response, cancellationToken);
            throw new InvalidOperationException($"OneDrive rejected the captured sign-in token. HTTP {(int)response.StatusCode}: {body}");
        }
    }

    public async IAsyncEnumerable<LivePhotoItem> ScanAsync(
        string localScanRoot,
        string cloudPathToScan,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        localScanRoot = Path.GetFullPath(localScanRoot);
        cloudPathToScan = NormalizeCloudPath(cloudPathToScan);

        await foreach (var item in ScanFolderAsync(
            localScanRoot,
            cloudPathToScan,
            currentCloudPath: "\\",
            elementId: string.Empty,
            driveId: string.Empty,
            uri: string.Empty,
            cancellationToken))
        {
            yield return item;
        }
    }

    public Task<string> DownloadMovAsync(
        LivePhotoItem item,
        CancellationToken cancellationToken = default,
        IProgress<FileTransferProgress>? progress = null,
        string? progressKey = null)
    {
        Directory.CreateDirectory(item.LocalFolderPath);
        var target = ChooseSafeMovTarget(item);

        if (IsExistingFileGood(target, item.ExpectedVideoSize, unknownLengthMinimum: 64 * 1024))
        {
            progress?.Report(new FileTransferProgress(progressKey ?? item.ItemId, Math.Max(item.ExpectedVideoSize, 0), Math.Max(item.ExpectedVideoSize, 0), item.Name));
            return Task.FromResult(target);
        }

        var uri = BuildContentUri(item, formatVideo: true);
        return DownloadContentAsync(uri, target, item.ExpectedVideoSize, item.TakenOrModified, cancellationToken, allowReplaceBadExisting: true, progress, progressKey ?? item.ItemId, item.Name);
    }

    public Task<string> DownloadImageAsync(
        LivePhotoItem item,
        string targetPath,
        CancellationToken cancellationToken = default,
        IProgress<FileTransferProgress>? progress = null,
        string? progressKey = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? ".");

        if (IsExistingFileGood(targetPath, item.ImageSize, unknownLengthMinimum: 1))
        {
            progress?.Report(new FileTransferProgress(progressKey ?? item.ItemId + ":image", Math.Max(item.ImageSize, 0), Math.Max(item.ImageSize, 0), item.Name));
            return Task.FromResult(targetPath);
        }

        var uri = BuildContentUri(item, formatVideo: false);
        return DownloadContentAsync(uri, targetPath, item.ImageSize, item.TakenOrModified, cancellationToken, allowReplaceBadExisting: false, progress, progressKey ?? item.ItemId + ":image", item.Name);
    }

    private async Task<string> DownloadContentAsync(
        string uri,
        string targetPath,
        long expectedLength,
        DateTimeOffset lastModified,
        CancellationToken cancellationToken,
        bool allowReplaceBadExisting,
        IProgress<FileTransferProgress>? progress,
        string progressKey,
        string progressLabel)
    {
        if (File.Exists(targetPath) && !allowReplaceBadExisting)
        {
            progress?.Report(new FileTransferProgress(progressKey, Math.Max(expectedLength, 0), Math.Max(expectedLength, 0), progressLabel));
            return targetPath;
        }

        var temp = targetPath + "." + Guid.NewGuid().ToString("N") + ".download";
        if (File.Exists(temp)) File.Delete(temp);

        try
        {
            using var response = await SendGetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(response, cancellationToken);
                throw new InvalidOperationException($"OneDrive content download failed. HTTP {(int)response.StatusCode}: {body}");
            }

            var totalBytes = expectedLength > 0 ? expectedLength : response.Content.Headers.ContentLength ?? -1;
            var buffer = new byte[1024 * 1024];
            long actualLength = 0;

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var fs = new FileStream(
                             temp,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 1024 * 1024,
                             options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                    if (read == 0) break;
                    await fs.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    actualLength += read;
                    progress?.Report(new FileTransferProgress(progressKey, actualLength, Math.Max(totalBytes, 0), progressLabel));
                }
            }

            if (expectedLength > 0 && actualLength != expectedLength)
            {
                File.Delete(temp);
                throw new InvalidOperationException(
                    $"Downloaded file size mismatch. Got {actualLength:N0} bytes; expected {expectedLength:N0} bytes.");
            }

            File.SetLastWriteTimeUtc(temp, lastModified.UtcDateTime);
            if (File.Exists(targetPath) && allowReplaceBadExisting) File.Delete(targetPath);
            File.Move(temp, targetPath);
            progress?.Report(new FileTransferProgress(progressKey, actualLength, Math.Max(totalBytes, 0), progressLabel));
            return targetPath;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private async IAsyncEnumerable<LivePhotoItem> ScanFolderAsync(
        string localScanRoot,
        string cloudPathToScan,
        string currentCloudPath,
        string elementId,
        string driveId,
        string uri,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        uri = string.IsNullOrEmpty(uri) ? BuildChildrenUri(elementId, driveId) : uri;

        using var response = await SendGetAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(response, cancellationToken);
            throw new InvalidOperationException($"OneDrive API scan failed. HTTP {(int)response.StatusCode}: {body}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = json.RootElement;

        if (root.TryGetProperty("value", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in values.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var name = GetString(entry, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;

                var childCloudFolder = CombineCloudPath(currentCloudPath, name, isFolder: true);

                if (entry.TryGetProperty("folder", out _))
                {
                    if (PathsOverlap(childCloudFolder, cloudPathToScan))
                    {
                        var childId = GetString(entry, "id");
                        if (!string.IsNullOrWhiteSpace(childId))
                        {
                            await foreach (var child in ScanFolderAsync(localScanRoot, cloudPathToScan, childCloudFolder, childId, driveId, string.Empty, cancellationToken))
                            {
                                yield return child;
                            }
                        }
                    }
                    continue;
                }

                if (entry.TryGetProperty("remoteItem", out var remoteItem))
                {
                    if (PathsOverlap(childCloudFolder, cloudPathToScan))
                    {
                        var childId = GetString(remoteItem, "id");
                        var remoteDriveId = string.Empty;
                        if (remoteItem.TryGetProperty("parentReference", out var parentRef))
                        {
                            remoteDriveId = GetString(parentRef, "driveId");
                        }

                        if (!string.IsNullOrWhiteSpace(childId))
                        {
                            await foreach (var child in ScanFolderAsync(localScanRoot, cloudPathToScan, childCloudFolder, childId, remoteDriveId, string.Empty, cancellationToken))
                            {
                                yield return child;
                            }
                        }
                    }
                    continue;
                }

                if (!currentCloudPath.StartsWith(cloudPathToScan, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!entry.TryGetProperty("photo", out var photo) ||
                    !photo.TryGetProperty("livePhoto", out var livePhoto))
                {
                    continue;
                }

                var itemId = GetString(entry, "id");
                if (string.IsNullOrWhiteSpace(itemId)) continue;

                var imageSize = GetInt64(entry, "size", -1);
                var totalStreamSize = GetInt64(livePhoto, "totalStreamSize", -1);
                var expectedVideoSize = totalStreamSize > imageSize && imageSize > 0 ? totalStreamSize - imageSize : -1;
                var localFolder = MapCloudFolderToLocal(localScanRoot, cloudPathToScan, currentCloudPath);

                var taken = DateTimeOffset.Now;
                if (photo.TryGetProperty("takenDateTime", out var takenProp) &&
                    DateTimeOffset.TryParse(takenProp.GetString(), out var parsedTaken))
                {
                    taken = parsedTaken;
                }
                else if (entry.TryGetProperty("fileSystemInfo", out var fileSystemInfo) &&
                         fileSystemInfo.TryGetProperty("lastModifiedDateTime", out var modifiedProp) &&
                         DateTimeOffset.TryParse(modifiedProp.GetString(), out var parsedModified))
                {
                    taken = parsedModified;
                }

                var item = new LivePhotoItem
                {
                    Name = name,
                    ItemId = itemId,
                    DriveId = driveId,
                    CloudFolderPath = currentCloudPath,
                    LocalFolderPath = localFolder,
                    ImageSize = imageSize,
                    ExpectedVideoSize = expectedVideoSize,
                    TakenOrModified = taken
                };
                item.RefreshMovState();
                if (!item.MovDownloaded) item.Status = "MOV missing";
                yield return item;
            }
        }

        if (root.TryGetProperty("@odata.nextLink", out var nextLink) && nextLink.ValueKind == JsonValueKind.String)
        {
            var next = nextLink.GetString();
            if (!string.IsNullOrWhiteSpace(next))
            {
                await foreach (var item in ScanFolderAsync(localScanRoot, cloudPathToScan, currentCloudPath, elementId, driveId, next, cancellationToken))
                {
                    yield return item;
                }
            }
        }
    }

    private async Task<HttpResponseMessage> SendGetAsync(string uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Authorization", _accessToken);
        request.Headers.TryAddWithoutValidation("Prefer", "Include-Feature=AddToOneDrive");
        request.Headers.TryAddWithoutValidation("User-Agent", "OneDriveLivePhotoViewer/2.6");
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static string BuildContentUri(LivePhotoItem item, bool formatVideo)
    {
        var contentSuffix = formatVideo ? "/content?format=video" : "/content";
        return string.IsNullOrEmpty(item.DriveId)
            ? $"https://my.microsoftpersonalcontent.com/_api/v2.1/drive/items/{Uri.EscapeDataString(item.ItemId)}{contentSuffix}"
            : $"https://my.microsoftpersonalcontent.com/_api/v2.1/drives/{Uri.EscapeDataString(item.DriveId)}/items/{Uri.EscapeDataString(item.ItemId)}{contentSuffix}";
    }

    private static string BuildChildrenUri(string elementId, string driveId)
    {
        var location = string.IsNullOrWhiteSpace(elementId) ? "root" : "items/" + Uri.EscapeDataString(elementId);
        const string query = "children?%24filter=photo%2FlivePhoto+ne+null+or+folder+ne+null+or+remoteItem+ne+null" +
                             "&select=fileSystemInfo%2Cphoto%2Cid%2Cname%2Csize%2Cfolder%2CremoteItem";

        return string.IsNullOrWhiteSpace(driveId)
            ? $"https://my.microsoftpersonalcontent.com/_api/v2.1/drive/{location}/{query}"
            : $"https://my.microsoftpersonalcontent.com/_api/v2.1/drives/{Uri.EscapeDataString(driveId)}/{location}/{query}";
    }

    private static string NormalizeCloudPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "\\";
        path = path.Replace('/', '\\');
        if (!path.StartsWith('\\')) path = "\\" + path;
        if (!path.EndsWith('\\')) path += "\\";
        return path;
    }

    private static string CombineCloudPath(string current, string name, bool isFolder)
    {
        current = NormalizeCloudPath(current);
        return current + name + (isFolder ? "\\" : string.Empty);
    }

    private static bool PathsOverlap(string folderPath, string targetPath)
    {
        folderPath = NormalizeCloudPath(folderPath);
        targetPath = NormalizeCloudPath(targetPath);
        return folderPath.StartsWith(targetPath, StringComparison.OrdinalIgnoreCase) ||
               targetPath.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string MapCloudFolderToLocal(string localScanRoot, string cloudScanRoot, string currentCloudFolder)
    {
        cloudScanRoot = NormalizeCloudPath(cloudScanRoot);
        currentCloudFolder = NormalizeCloudPath(currentCloudFolder);
        var rel = currentCloudFolder.StartsWith(cloudScanRoot, StringComparison.OrdinalIgnoreCase)
            ? currentCloudFolder[cloudScanRoot.Length..]
            : string.Empty;

        rel = rel.Trim('\\');
        if (string.IsNullOrWhiteSpace(rel)) return localScanRoot;
        var parts = new[] { localScanRoot }.Concat(rel.Split('\\', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        return Path.Combine(parts);
    }

    private static string ChooseSafeMovTarget(LivePhotoItem item)
    {
        if (IsExistingFileGood(item.PreferredMovPath, item.ExpectedVideoSize, unknownLengthMinimum: 64 * 1024)) return item.PreferredMovPath;
        if (!File.Exists(item.PreferredMovPath)) return item.PreferredMovPath;
        if (IsExistingFileGood(item.AlternateMovPath, item.ExpectedVideoSize, unknownLengthMinimum: 64 * 1024)) return item.AlternateMovPath;

        // Avoid destructive overwrite of an unrelated existing .mov with the same basename.
        return item.AlternateMovPath;
    }

    private static bool IsExistingFileGood(string path, long expectedLength, long unknownLengthMinimum)
    {
        if (!File.Exists(path)) return false;
        var length = new FileInfo(path).Length;
        return expectedLength > 0 ? length == expectedLength : length >= unknownLengthMinimum;
    }

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static long GetInt64(JsonElement element, string name, long fallback)
    {
        if (!element.TryGetProperty(name, out var property)) return fallback;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var value) => value,
            JsonValueKind.String when long.TryParse(property.GetString(), out var value) => value,
            _ => fallback
        };
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (body.Length > 600) body = body[..600] + "...";
            return string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase ?? "No response body" : body;
        }
        catch
        {
            return response.ReasonPhrase ?? "No response body";
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // best effort cleanup
        }
    }

    public void Dispose() => _http.Dispose();
}
