using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     Represents a single changed file within a pull request.
/// </summary>
public sealed record ChangedFile
{
    /// <summary>
    ///     Creates a new <see cref="ChangedFile" />.
    /// </summary>
    public ChangedFile(string path, ChangeType changeType, string fullContent, string unifiedDiff)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("Path required.", nameof(path));
        }

        this.Path = path;
        this.ChangeType = changeType;
        this.FullContent = fullContent ?? "";
        this.UnifiedDiff = unifiedDiff ?? "";
    }

    /// <summary>File path relative to the repository root.</summary>
    public string Path { get; init; }

    /// <summary>Type of change that was made to the file.</summary>
    public ChangeType ChangeType { get; init; }

    /// <summary>Full content of the file, if available.</summary>
    public string FullContent { get; init; }

    /// <summary>Unified diff for the file.</summary>
    public string UnifiedDiff { get; init; }
}