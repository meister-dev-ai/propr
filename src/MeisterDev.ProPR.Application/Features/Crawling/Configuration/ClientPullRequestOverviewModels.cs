// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;

namespace MeisterDev.ProPR.Application.Features.Crawling.Configuration;

/// <summary>Source coordinates selected by an authenticated client caller.</summary>
public sealed record ClientPullRequestOverviewSource(Guid TargetId, Guid ConnectionId);

/// <summary>Client-scoped source selection, generation navigation and row bounds.</summary>
public sealed record ClientPullRequestOverviewRequest(
    IReadOnlyList<ClientPullRequestOverviewSource> Sources,
    string Binding,
    string? Cursor = null,
    int Page = 1,
    int PageSize = 25,
    bool LoadMore = false,
    bool Reload = false);

/// <summary>Discovery and metadata observations for one pull request revision.</summary>
/// <param name="Number">Positive provider-native pull request or merge request number required by installed adapter operations.</param>
public sealed record ClientPullRequestOverviewRow(
    Guid TargetId,
    Guid ConnectionId,
    int Number,
    string Title,
    string? WebUrl,
    string State,
    string? SourceBranch,
    string? TargetBranch,
    string? AuthorName,
    string? HeadSha,
    string? ProviderRevisionId,
    ReviewOverviewDto? Metadata,
    string MetadataStatus,
    DateTimeOffset ListedAt,
    DateTimeOffset? MetadataAt)
{
    /// <summary>Authorized source associations for a deduplicated pull request.</summary>
    public IReadOnlyList<ClientPullRequestOverviewSource> Associations { get; init; } = [];
}

/// <summary>Current source coverage and permitted refresh time.</summary>
public sealed record ClientPullRequestOverviewOutcome(
    Guid TargetId,
    Guid ConnectionId,
    string Status,
    string? FailureKind,
    DateTimeOffset? ListedAt,
    DateTimeOffset NextRefreshAt);

/// <summary>An immutable cumulative page with explicit freshness and coverage.</summary>
public sealed record ClientPullRequestOverviewPage(
    IReadOnlyList<ClientPullRequestOverviewRow> Items,
    IReadOnlyList<ClientPullRequestOverviewOutcome> Sources,
    string Cursor,
    string? NextCursor,
    int Page,
    int PageSize,
    int? TotalRows,
    int TotalSources,
    int AttemptedSources,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    bool IsStale,
    DateTimeOffset? NextRefreshAt,
    string Status = "available",
    int PerSourceLimit = 100);

/// <summary>Invalid, obsolete or unauthorized overview request.</summary>
public sealed class ClientPullRequestOverviewException(string kind) : Exception("The pull request page is unavailable.")
{
    public string Kind { get; } = kind;
}
