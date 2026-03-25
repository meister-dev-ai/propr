namespace MeisterProPR.Infrastructure;

/// <summary>
///     Detects binary files by extension so their content is never sent to the AI.
///     Binary files are listed by name and change type only.
/// </summary>
/// <remarks>
///     This method of detection is too simplified and needs to be replaced by a proper detection in the future.
/// </remarks>
internal static class BinaryFileDetector
{
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Images
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tiff", ".tif", ".ico", ".webp",
        ".psd", ".ai", ".raw", ".heic", ".heif",

        // Archives
        ".zip", ".tar", ".gz", ".tgz", ".7z", ".rar", ".bz2", ".xz", ".lz4", ".zst",
        ".nupkg", ".snupkg",

        // Compiled / native
        ".exe", ".dll", ".so", ".dylib", ".bin", ".obj", ".o", ".a", ".lib",
        ".class", ".pyc", ".pyo", ".pyd",

        // Debug symbols / build artifacts
        ".pdb", ".ilk", ".exp",

        // Documents
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".odt", ".ods", ".odp",

        // Media
        ".mp3", ".mp4", ".wav", ".avi", ".mov", ".mkv", ".flac", ".ogg",
        ".wma", ".aac", ".m4a", ".m4v", ".webm",

        // Fonts
        ".ttf", ".otf", ".woff", ".woff2", ".eot",

        // Certificates / keys
        ".p12", ".pfx", ".cer", ".der",

        // Database
        ".db", ".sqlite", ".mdf", ".ldf",
    };

    /// <summary>Returns <see langword="true" /> when <paramref name="path" /> has a known binary extension.</summary>
    public static bool IsBinary(string path)
    {
        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && BinaryExtensions.Contains(ext);
    }
}
