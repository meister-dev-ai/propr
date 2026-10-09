// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.ProCursor.Contracts.Sources;

namespace MeisterDev.ProPR.Application.Features.Crawling.Configuration;

/// <summary>Resolves native guided configuration selections through existing provider capabilities.</summary>
public interface IReviewConfigurationSelectionService
{
    Task<ConnectionDiscoveryContext> GetConnectionContextAsync(Guid clientId, Guid connectionId, CancellationToken ct = default);
    Task<Guid> ResolveHistoricalConnectionAsync(Guid clientId, Guid savedScopeId, CancellationToken ct = default);
    ConnectionDiscoveryDescriptor GetDescriptor(ConnectionDiscoveryContext context);
    bool SupportsMentionConfiguration(ConnectionDiscoveryContext context);
    Task<IReadOnlyList<ConnectionDiscoveryScope>> GetScopesAsync(ConnectionDiscoveryContext context, CancellationToken ct = default);
    Task<IReadOnlyList<ScmDiscoveryProjectOption>> GetProjectsAsync(ConnectionDiscoveryContext context, string scopeKey, CancellationToken ct = default);

    Task<IReadOnlyList<ConnectionDiscoverySource>> GetSourcesAsync(
        ConnectionDiscoveryContext context, string scopeKey, string? projectId, ProCursorSourceKind kind, CancellationToken ct = default);

    Task<IReadOnlyList<ScmDiscoveryBranchOption>> GetBranchesAsync(
        ConnectionDiscoveryContext context, string scopeKey, string projectId, ProCursorSourceKind kind,
        CanonicalSourceReferenceDto reference, CancellationToken ct = default);

    Task<(ScmProvider Provider, Guid? OrganizationScopeId, string ProviderScopePath, string ProviderProjectKey)> ResolveConnectionSelectionAsync(
        Guid clientId, Guid connectionId, string scopeKey, string? projectId, ScmProvider? suppliedProvider,
        Guid? suppliedScopeId, string? suppliedScopePath, CancellationToken ct = default);

    Task<IReadOnlyList<CrawlRepoFilterDto>> ResolveConnectionFiltersAsync(
        ConnectionDiscoveryContext context, string scopeKey, string? projectId, IReadOnlyList<CrawlRepoFilterDto>? filters, CancellationToken ct = default);

    bool HasScopeSelection(ScmProvider provider, Guid? scopeId, string? path);

    bool HasScopeSelection(WebhookProviderType provider, Guid? scopeId, string? path);

    Task<(Guid? OrganizationScopeId, string OrganizationUrl)> ResolveScopeAsync(
        Guid clientId, ScmProvider provider, Guid? scopeId, string? path, CancellationToken ct, string configurationKind = "crawl");

    Task<(Guid? OrganizationScopeId, string OrganizationUrl)> ResolveScopeAsync(
        Guid clientId, WebhookProviderType provider, Guid? scopeId, string? path, CancellationToken ct, string configurationKind = "webhook");

    Task<IReadOnlyList<CrawlRepoFilterDto>> ResolveFiltersAsync(
        Guid clientId, ScmProvider provider, Guid? scopeId, string project, IReadOnlyList<CrawlRepoFilterDto>? filters, CancellationToken ct,
        string configurationKind = "crawl");

    Task<IReadOnlyList<CrawlRepoFilterDto>> ResolveFiltersAsync(
        Guid clientId, WebhookProviderType provider, Guid? scopeId, string project, IReadOnlyList<CrawlRepoFilterDto>? filters, CancellationToken ct,
        string configurationKind = "webhook");

    Task<GuidedSourceSelection> ResolveGuidedSourceAsync(
        Guid clientId, ScmProvider provider, Guid scopeId, string project, ProCursorSourceKind kind, CanonicalSourceReferenceDto reference,
        CancellationToken ct);
}

/// <summary>Saved source coordinates and discovered branch names.</summary>
public sealed record GuidedSourceSelection(ClientScmScopeDto Scope, ScmDiscoverySourceOption Source, IReadOnlyList<string> Branches);
