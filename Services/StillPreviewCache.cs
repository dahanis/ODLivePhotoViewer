using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OneDriveLivePhotoViewer.Models;

namespace OneDriveLivePhotoViewer.Services;

public static class StillPreviewCache
{
    public static string CacheDirectory { get; } = Path.Combine(AppLogger.AppDataDirectory, "PreviewCache");

    private static readonly HashSet<string> WebFriendlyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp"
    };

    public static async Task<string> GetPreviewImageAsync(LivePhotoItem item, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(item.ImagePath))
        {
            throw new FileNotFoundException("The still image is not present locally.", item.ImagePath);
        }

        var extension = Path.GetExtension(item.ImagePath);
        if (WebFriendlyExtensions.Contains(extension))
        {
            return item.ImagePath;
        }

        Directory.CreateDirectory(CacheDirectory);
        var sourceInfo = new FileInfo(item.ImagePath);
        var cacheName = BuildCacheKey(sourceInfo.FullName, sourceInfo.Length, sourceInfo.LastWriteTimeUtc) + ".jpg";
        var target = Path.Combine(CacheDirectory, cacheName);
        if (File.Exists(target) && new FileInfo(target).Length > 0)
        {
            return target;
        }

        await Task.Run(() => ConvertToJpegPreview(sourceInfo.FullName, target, cancellationToken), cancellationToken);
        return target;
    }

    private static void ConvertToJpegPreview(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var temp = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.SequentialScan);
            var decoder = BitmapDecoder.Create(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.FirstOrDefault() ?? throw new InvalidOperationException("The still image could not be decoded.");

            BitmapSource output = frame;
            const double maxSide = 4096.0;
            var largestSide = Math.Max(frame.PixelWidth, frame.PixelHeight);
            if (largestSide > maxSide)
            {
                var scale = maxSide / largestSide;
                var transformed = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
                transformed.Freeze();
                output = transformed;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
            encoder.Frames.Add(BitmapFrame.Create(output));

            using (var destination = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                encoder.Save(destination);
            }

            if (File.Exists(targetPath)) File.Delete(targetPath);
            File.Move(temp, targetPath);
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidOperationException(
                "Windows could not decode this still image. For HEIC/HEIF files, install Microsoft's HEIF Image Extensions and HEVC Video Extensions, then reopen the app.", ex);
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

    private static string BuildCacheKey(string path, long length, DateTime lastWriteUtc)
    {
        var raw = path + "|" + length + "|" + lastWriteUtc.Ticks;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
