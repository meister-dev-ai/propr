// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     One explicitly tracked moving repository state for a ProCursor knowledge source.
/// </summary>
public sealed class ProCursorTrackedBranch
{
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

    public Guid Id { get; private set; }

    public Guid KnowledgeSourceId { get; private set; }

    public string BranchName { get; private set; } = string.Empty;

    public ProCursorRefreshTriggerMode RefreshTriggerMode { get; private set; }

    public bool MiniIndexEnabled { get; private set; }

    public string? LastSeenCommitSha { get; private set; }

    public string? LastIndexedCommitSha { get; private set; }

    public bool IsEnabled { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

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

    public void RecordSeenCommit(string commitSha)
    {
        this.LastSeenCommitSha = NormalizeCommit(commitSha);
        this.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void RecordIndexedCommit(string commitSha)
    {
        var normalizedCommit = NormalizeCommit(commitSha);
        this.LastSeenCommitSha ??= normalizedCommit;
        this.LastIndexedCommitSha = normalizedCommit;
        this.UpdatedAt = DateTimeOffset.UtcNow;
    }

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
