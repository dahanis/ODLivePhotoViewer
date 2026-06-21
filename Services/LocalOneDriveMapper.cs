using System.IO;
using Microsoft.Win32;

namespace OneDriveLivePhotoViewer.Services;

public sealed record OneDrivePathMapping(
    string LocalRoot,
    string SelectedLocalPath,
    string CloudPath,
    string DisplayPath,
    string? SpecificFileName = null)
{
    public bool IsSpecificFile => !string.IsNullOrWhiteSpace(SpecificFileName);
}

public static class LocalOneDriveMapper
{
    public static OneDrivePathMapping? TryMapLocalFolder(string selectedPath)
    {
        selectedPath = Path.GetFullPath(selectedPath.Trim());
        var root = FindOneDriveRoot(selectedPath);
        if (root is null) return null;

        var relative = Path.GetRelativePath(root, selectedPath);
        var cloud = relative == "."
            ? "\\"
            : "\\" + relative.Replace(Path.DirectorySeparatorChar, '\\').Replace(Path.AltDirectorySeparatorChar, '\\').Trim('\\') + "\\";

        return new OneDrivePathMapping(root, selectedPath, cloud, selectedPath);
    }

    public static OneDrivePathMapping? TryMapLocalFile(string selectedFile)
    {
        selectedFile = Path.GetFullPath(selectedFile.Trim());
        var parent = Path.GetDirectoryName(selectedFile);
        if (string.IsNullOrWhiteSpace(parent)) return null;

        var root = FindOneDriveRoot(selectedFile);
        if (root is null) return null;

        var relativeParent = Path.GetRelativePath(root, parent);
        var cloudParent = relativeParent == "."
            ? "\\"
            : "\\" + relativeParent.Replace(Path.DirectorySeparatorChar, '\\').Replace(Path.AltDirectorySeparatorChar, '\\').Trim('\\') + "\\";

        return new OneDrivePathMapping(
            root,
            parent,
            cloudParent,
            selectedFile,
            Path.GetFileName(selectedFile));
    }

    private static string? FindOneDriveRoot(string selectedPath)
    {
        var candidates = GetCandidateRoots()
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => path.Length)
            .ToList();

        foreach (var candidate in candidates)
        {
            if (IsSameOrChild(selectedPath, candidate)) return candidate;
        }

        // Fallback: climb the folder tree and accept common OneDrive sync-root folder names.
        var current = new DirectoryInfo(File.Exists(selectedPath) ? Path.GetDirectoryName(selectedPath)! : selectedPath);
        while (current is not null)
        {
            if (current.Name.Equals("OneDrive", StringComparison.OrdinalIgnoreCase) ||
                current.Name.StartsWith("OneDrive -", StringComparison.OrdinalIgnoreCase))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        return null;
    }

    private static IEnumerable<string?> GetCandidateRoots()
    {
        yield return Environment.GetEnvironmentVariable("OneDrive");
        yield return Environment.GetEnvironmentVariable("OneDriveConsumer");
        yield return Environment.GetEnvironmentVariable("OneDriveCommercial");

        foreach (var account in new[] { "Personal", "Business1", "Business2", "Business3", "Business4" })
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\OneDrive\Accounts\{account}");
            yield return key?.GetValue("UserFolder") as string;
        }
    }

    private static bool IsSameOrChild(string path, string root)
    {
        var fullPath = EnsureTrailingSlash(Path.GetFullPath(path));
        var fullRoot = EnsureTrailingSlash(Path.GetFullPath(root));
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSlash(string path)
        => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}
