using System.IO;
using System.Text;

namespace OneDriveLivePhotoViewer.Services;

public static class AppLogger
{
    public static string AppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OneDriveLivePhotoViewer");

    public static string LogFilePath { get; } = Path.Combine(AppDataDirectory, "app.log");

    public static void Log(string message, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(AppDataDirectory);
            var builder = new StringBuilder();
            builder.Append('[').Append(DateTimeOffset.Now.ToString("u")).Append("] ").AppendLine(message);
            if (exception is not null)
            {
                builder.AppendLine(exception.ToString());
            }
            File.AppendAllText(LogFilePath, builder.ToString());
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
