// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Infrastructure.ProCursor;

/// <summary>
///     Detects binary files by extension so ProCursor indexing skips non-text content.
/// </summary>
internal static class BinaryFileDetector
{
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tiff", ".tif", ".ico", ".webp",
        ".psd", ".ai", ".raw", ".heic", ".heif",
        ".zip", ".tar", ".gz", ".tgz", ".7z", ".rar", ".bz2", ".xz", ".lz4", ".zst",
        ".nupkg", ".snupkg",
        ".exe", ".dll", ".so", ".dylib", ".bin", ".obj", ".o", ".a", ".lib",
        ".class", ".pyc", ".pyo", ".pyd",
        ".pdb", ".ilk", ".exp",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".odt", ".ods", ".odp",
        ".mp3", ".mp4", ".wav", ".avi", ".mov", ".mkv", ".flac", ".ogg",
        ".wma", ".aac", ".m4a", ".m4v", ".webm",
        ".ttf", ".otf", ".woff", ".woff2", ".eot",
        ".p12", ".pfx", ".cer", ".der",
        ".db", ".sqlite", ".mdf", ".ldf",
    };

    public static bool IsBinary(string path)
    {
        var extension = Path.GetExtension(path);
        return !string.IsNullOrEmpty(extension) && BinaryExtensions.Contains(extension);
    }
}
