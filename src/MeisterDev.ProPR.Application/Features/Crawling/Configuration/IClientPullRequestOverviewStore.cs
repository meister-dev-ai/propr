// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Crawling.Configuration;

/// <summary>Durable source coordination and immutable client-scoped discovery generations.</summary>
public interface IClientPullRequestOverviewStore
{
    /// <summary>Returns the durable client cursor version advanced by a fenced provider denial.</summary>
    Task<string?> ReadInvalidationAsync(Guid clientId, CancellationToken ct);

    Task<OverviewSourceClaim> AcquireAsync(
        Guid clientId, string key, string fingerprint, DateTimeOffset now, CancellationToken ct, OverviewCacheScope? scope = null);

    Task<bool> CompleteAsync(
        Guid clientId, string key, Guid owner, string fingerprint, string? content, string? failure, DateTimeOffset now, CancellationToken ct);

    Task<bool> SaveGenerationAsync(
        Guid clientId, Guid generation, string binding, string content, DateTimeOffset now, CancellationToken ct, DateTimeOffset? expiresAt = null);

    Task<string?> ReadGenerationAsync(Guid clientId, Guid generation, string binding, DateTimeOffset now, CancellationToken ct);

    /// <summary>Reads immutable content with its authoritative persisted retention deadline.</summary>
    Task<OverviewGeneration?> ReadGenerationStateAsync(Guid clientId, Guid generation, string binding, DateTimeOffset now, CancellationToken ct);

    /// <summary>Shares an equivalent active generation or stores a fresh opaque incarnation atomically.</summary>
    Task<OverviewGeneration?> GetOrCreateGenerationAsync(
        Guid clientId, string binding, string equivalence, string content, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken ct);

    Task<string?> ReadLatestGenerationAsync(Guid clientId, string binding, DateTimeOffset now, CancellationToken ct);
    Task<bool> SavePageAsync(Guid clientId, string key, string binding, string content, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken ct);
    Task<string?> ReadPageAsync(Guid clientId, string key, string binding, DateTimeOffset now, CancellationToken ct);
}

/// <summary>Shared refresh ownership and previously observed content.</summary>
public sealed record OverviewSourceClaim(
    Guid? Owner,
    string? Content,
    string? Failure,
    DateTimeOffset? ObservedAt,
    DateTimeOffset NextRefreshAt,
    DateTimeOffset? LeaseUntil);

/// <summary>Non-secret provenance used to fence observations after provider access loss.</summary>
public sealed record OverviewCacheScope(Guid ConnectionId, string SourceKey);

/// <summary>Immutable generation content and its persisted identity and lifetime.</summary>
public sealed record OverviewGeneration(Guid Id, string Content, DateTimeOffset ExpiresAt);
