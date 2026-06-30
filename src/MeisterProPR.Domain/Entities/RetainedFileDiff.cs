// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     A retained per-file unified diff under a <see cref="RetainedPullRequest" />, captured for a
///     specific review increment. The diff text is the only sensitive field and is stored encrypted
///     at rest in <see cref="EncryptedUnifiedDiff" />; all structured metadata stays plaintext.
/// </summary>
public sealed class RetainedFileDiff
{
    /// <summary>Unique identifier for this retained file diff.</summary>
    public Guid Id { get; init; }

    /// <summary>Owning retained pull request.</summary>
    public Guid RetainedPullRequestId { get; init; }

    /// <summary>The review increment this diff belongs to. Unique per (pull request, revision, file path).</summary>
    public string RevisionKey { get; init; } = string.Empty;

    /// <summary>The file path the diff applies to.</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>The kind of change, e.g. "add", "edit", "delete", "rename".</summary>
    public string ChangeType { get; set; } = string.Empty;

    /// <summary>Whether the file is binary (and therefore has no meaningful textual diff).</summary>
    public bool IsBinary { get; set; }

    /// <summary>The canonical unified diff for the file, stored encrypted at rest.</summary>
    public string EncryptedUnifiedDiff { get; set; } = string.Empty;

    /// <summary>UTC timestamp when this retained file diff was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Navigation back to the owning retained pull request.</summary>
    public RetainedPullRequest? RetainedPullRequest { get; init; }
}
