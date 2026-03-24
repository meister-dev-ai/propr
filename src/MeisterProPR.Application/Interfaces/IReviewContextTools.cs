using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Provides tools that the AI reviewer can invoke during an agentic review pass
///     to retrieve information about the pull request and repository.
/// </summary>
public interface IReviewContextTools
{
    /// <summary>Returns the list of files changed in the pull request being reviewed.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ChangedFileSummary>> GetChangedFilesAsync(CancellationToken ct);

    /// <summary>Returns the file tree for the specified branch.</summary>
    /// <param name="branch">Branch name to enumerate files from.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<string>> GetFileTreeAsync(string branch, CancellationToken ct);

    /// <summary>
    ///     Returns a range of lines from a file at a specific branch.
    ///     Returns an error string if the file exceeds the configured size limit.
    /// </summary>
    /// <param name="path">Repository-relative file path.</param>
    /// <param name="branch">Branch name to read the file from.</param>
    /// <param name="startLine">One-based line number to start reading from.</param>
    /// <param name="endLine">One-based line number to stop reading at (inclusive).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string> GetFileContentAsync(string path, string branch, int startLine, int endLine, CancellationToken ct);
}
