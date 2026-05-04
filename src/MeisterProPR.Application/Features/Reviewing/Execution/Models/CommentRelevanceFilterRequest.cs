// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Normalized per-file input passed from the orchestrator into a comment relevance filter.
/// </summary>
public sealed record CommentRelevanceFilterRequest
{
    /// <summary>
    ///     Creates a new <see cref="CommentRelevanceFilterRequest" />.
    /// </summary>
    public CommentRelevanceFilterRequest(
        Guid jobId,
        Guid? fileResultId,
        string? selectedImplementationId,
        string filePath,
        ChangedFile file,
        PullRequest pullRequest,
        IReadOnlyList<ReviewComment> comments,
        ReviewSystemContext reviewContext,
        Guid? protocolId)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path is required.", nameof(filePath));
        }

        this.JobId = jobId;
        this.FileResultId = fileResultId;
        this.SelectedImplementationId = selectedImplementationId;
        this.FilePath = filePath;
        this.File = file;
        this.PullRequest = pullRequest;
        this.Comments = comments ?? [];
        this.ReviewContext = reviewContext ?? throw new ArgumentNullException(nameof(reviewContext));
        this.ProtocolId = protocolId;
    }

    /// <summary>The owning review job identifier.</summary>
    public Guid JobId { get; }

    /// <summary>The current per-file result identifier when available.</summary>
    public Guid? FileResultId { get; }

    /// <summary>The selected implementation identifier for this run, or <see langword="null" />.</summary>
    public string? SelectedImplementationId { get; }

    /// <summary>The current file path under review.</summary>
    public string FilePath { get; }

    /// <summary>The changed-file payload under review.</summary>
    public ChangedFile File { get; }

    /// <summary>The pull request view available to this file-level review pass.</summary>
    public PullRequest PullRequest { get; }

    /// <summary>The normalized per-file review comments after earlier hard guards have run.</summary>
    public IReadOnlyList<ReviewComment> Comments { get; }

    /// <summary>The shared per-file review context available to the filter.</summary>
    public ReviewSystemContext ReviewContext { get; }

    /// <summary>The active protocol identifier for the current file pass when diagnostics are enabled.</summary>
    public Guid? ProtocolId { get; }
}
