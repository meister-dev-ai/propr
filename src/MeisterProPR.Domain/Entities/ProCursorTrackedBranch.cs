// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     One explicitly tracked moving repository state for a ProCursor knowledge source.
/// </summary>
public sealed class ProCursorTrackedBranch
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="ProCursorTrackedBranch"/> class.
    /// </summary>
    /// <param name="id">The unique identifier for this tracked branch.</param>
    /// <param name="knowledgeSourceId">The unique identifier of the knowledge source.</param>
    /// <param name="branchName">The name of the branch to track.</param>
    /// <param name="refreshTriggerMode">The mode that triggers refreshes for this branch.</param>
    /// <param name="miniIndexEnabled">Whether mini indexing is enabled.</param>
    /// <exception cref="ArgumentException">Thrown when id or knowledgeSourceId is empty, or branchName is null or whitespace.</exception>
    public ProCursorTrackedBranch(
        Guid id,
        Guid knowledgeSourceId,
        string branchName,
        ProCursorRefreshTriggerMode refreshTriggerMode,
        bool miniIndexEnabled)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", nameof(id));
        }

        if (knowledgeSourceId == Guid.Empty)
        {
            throw new ArgumentException("KnowledgeSourceId must not be empty.", nameof(knowledgeSourceId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        this.Id = id;
        this.KnowledgeSourceId = knowledgeSourceId;
        this.BranchName = branchName.Trim();
        this.RefreshTriggerMode = refreshTriggerMode;
        this.MiniIndexEnabled = miniIndexEnabled;
        this.IsEnabled = true;
        this.CreatedAt = DateTimeOffset.UtcNow;
        this.UpdatedAt = this.CreatedAt;
    }

    private ProCursorTrackedBranch()
    {
    }

    /// <summary>
    ///     Gets the unique identifier for this tracked branch.
    /// </summary>
    public Guid Id { get; private set; }

    /// <summary>
    ///     Gets the unique identifier of the knowledge source.
    /// </summary>
    public Guid KnowledgeSourceId { get; private set; }

    /// <summary>
    ///     Gets the name of the branch being tracked.
    /// </summary>
    public string BranchName { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the mode that triggers refreshes for this branch.
    /// </summary>
    public ProCursorRefreshTriggerMode RefreshTriggerMode { get; private set; }

    /// <summary>
    ///     Gets a value indicating whether mini indexing is enabled.
    /// </summary>
    public bool MiniIndexEnabled { get; private set; }

    /// <summary>
    ///     Gets the SHA of the last seen commit.
    /// </summary>
    public string? LastSeenCommitSha { get; private set; }

    /// <summary>
    ///     Gets the SHA of the last indexed commit.
    /// </summary>
    public string? LastIndexedCommitSha { get; private set; }

    /// <summary>
    ///     Gets a value indicating whether this tracked branch is enabled.
    /// </summary>
    public bool IsEnabled { get; private set; }

    /// <summary>
    ///     Gets the date and time when this tracked branch was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    ///     Gets the date and time when this tracked branch was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    ///     Updates the settings for this tracked branch.
    /// </summary>
    /// <param name="refreshTriggerMode">The mode that triggers refreshes for this branch.</param>
    /// <param name="miniIndexEnabled">Whether mini indexing is enabled.</param>
    /// <param name="isEnabled">Whether this tracked branch is enabled, or null to leave unchanged.</param>
    public void UpdateSettings(
        ProCursorRefreshTriggerMode refreshTriggerMode,
        bool miniIndexEnabled,
        bool? isEnabled = null)
    {
        this.RefreshTriggerMode = refreshTriggerMode;
        this.MiniIndexEnabled = miniIndexEnabled;

        if (isEnabled.HasValue)
        {
            this.IsEnabled = isEnabled.Value;
        }

        this.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    ///     Records the last seen commit SHA for this tracked branch.
    /// </summary>
    /// <param name="commitSha">The SHA of the commit that was seen.</param>
    public void RecordSeenCommit(string commitSha)
    {
        this.LastSeenCommitSha = NormalizeCommit(commitSha);
        this.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    ///     Records the last indexed commit SHA for this tracked branch.
    /// </summary>
    /// <param name="commitSha">The SHA of the commit that was indexed.</param>
    public void RecordIndexedCommit(string commitSha)
    {
        var normalizedCommit = NormalizeCommit(commitSha);
        this.LastSeenCommitSha ??= normalizedCommit;
        this.LastIndexedCommitSha = normalizedCommit;
        this.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    ///     Sets whether this tracked branch is enabled.
    /// </summary>
    /// <param name="isEnabled">Whether this tracked branch should be enabled.</param>
    public void SetEnabled(bool isEnabled)
    {
        this.IsEnabled = isEnabled;
        this.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static string NormalizeCommit(string commitSha)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);
        return commitSha.Trim();
    }
}
