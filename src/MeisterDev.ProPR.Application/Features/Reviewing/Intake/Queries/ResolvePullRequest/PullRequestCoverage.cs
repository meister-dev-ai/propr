// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.DTOs.AzureDevOps;
using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Dtos;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Intake.Queries.ResolvePullRequest;

/// <summary>
///     One client's declared coverage of some repositories, flattened from whichever configuration
///     declared it.
/// </summary>
/// <remarks>
///     A repository reaches ProPR one of two ways, and either is sufficient: a crawl configuration
///     polls for its pull requests, or a webhook configuration receives them. Both record the same
///     three things this resolution needs — the client, the scope path and project key as the review
///     pipeline uses them, and the repositories covered by name — so the matching logic is written once
///     against this shape rather than twice against theirs.
/// </remarks>
/// <param name="ClientId">The client that owns the configuration.</param>
/// <param name="Provider">The provider family.</param>
/// <param name="ProviderScopePath">Scope path, stored verbatim as review-scoped endpoints expect it.</param>
/// <param name="ProviderProjectKey">Project, workspace, or namespace key.</param>
/// <param name="IsActive">Whether the configuration is currently in use.</param>
/// <param name="CoveredRepositories">
///     Repositories named by the configuration. Empty means the configuration covers its whole scope
///     without naming anything in it.
/// </param>
/// <param name="Source">Which kind of configuration this came from, for diagnostics.</param>
/// <param name="ProCursorSourceScopeMode">
///     Which code-knowledge sources a review started under this configuration may read. A review someone
///     asks for has to be the review the configuration describes, so these settings travel with the
///     coverage rather than being defaulted at the point of submission.
/// </param>
/// <param name="ProCursorSourceIds">The selected code-knowledge sources, when the scope is a selection.</param>
/// <param name="InvalidProCursorSourceIds">
///     Selected sources that no longer resolve. They suppress queuing rather than silently narrowing the
///     scope, so a review is never quietly run against less context than it was configured for.
/// </param>
/// <param name="ReviewTemperature">The review temperature this configuration reviews at, when it sets one.</param>
internal sealed record PullRequestCoverage(
    Guid ClientId,
    ScmProvider Provider,
    string ProviderScopePath,
    string ProviderProjectKey,
    bool IsActive,
    IReadOnlyList<CoveredRepository> CoveredRepositories,
    CoverageSource Source,
    ProCursorSourceScopeMode ProCursorSourceScopeMode = ProCursorSourceScopeMode.AllClientSources,
    IReadOnlyList<Guid>? ProCursorSourceIds = null,
    IReadOnlyList<Guid>? InvalidProCursorSourceIds = null,
    float? ReviewTemperature = null)
{
    public static PullRequestCoverage FromCrawlConfiguration(CrawlConfigurationDto configuration)
    {
        return new PullRequestCoverage(
            configuration.ClientId,
            configuration.Provider,
            configuration.ProviderScopePath,
            configuration.ProviderProjectKey,
            configuration.IsActive,
            configuration.RepoFilters.Select(CoveredRepository.FromCrawlFilter).ToList().AsReadOnly(),
            CoverageSource.CrawlConfiguration,
            configuration.ProCursorSourceScopeMode,
            configuration.ProCursorSourceIds,
            configuration.InvalidProCursorSourceIds,
            configuration.ReviewTemperature);
    }

    public static PullRequestCoverage FromWebhookConfiguration(WebhookConfigurationDto configuration)
    {
        // A webhook configuration carries no code-knowledge source scope of its own, so a review it covers
        // reads whatever the client has, exactly as a webhook-triggered review does today.
        return new PullRequestCoverage(
            configuration.ClientId,
            MapProvider(configuration.ProviderType),
            configuration.OrganizationUrl,
            configuration.ProjectId,
            configuration.IsActive,
            configuration.RepoFilters.Select(CoveredRepository.FromWebhookFilter).ToList().AsReadOnly(),
            CoverageSource.WebhookConfiguration,
            ReviewTemperature: configuration.ReviewTemperature);
    }

    private static ScmProvider MapProvider(WebhookProviderType providerType)
    {
        return providerType switch
        {
            WebhookProviderType.AzureDevOps => ScmProvider.AzureDevOps,
            WebhookProviderType.GitHub => ScmProvider.GitHub,
            WebhookProviderType.GitLab => ScmProvider.GitLab,
            WebhookProviderType.Forgejo => ScmProvider.Forgejo,
            _ => ScmProvider.AzureDevOps,
        };
    }
}

/// <summary>
///     A repository a configuration names, with its provider identity when the configuration happens to
///     record one.
/// </summary>
/// <param name="Name">Repository name, as it appears in a pull request's web address.</param>
/// <param name="ExternalRepositoryId">
///     Provider repository identity, when the configuration recorded one. Crawl configurations built
///     through guided discovery usually have it; webhook configurations usually do not, because a
///     webhook is registered by name.
/// </param>
internal sealed record CoveredRepository(string Name, string? ExternalRepositoryId)
{
    public static CoveredRepository FromCrawlFilter(CrawlRepoFilterDto filter)
    {
        return new CoveredRepository(
            string.IsNullOrWhiteSpace(filter.RepositoryName) ? filter.DisplayName ?? string.Empty : filter.RepositoryName,
            Identity(filter.CanonicalSourceRef));
    }

    public static CoveredRepository FromWebhookFilter(WebhookRepoFilterDto filter)
    {
        return new CoveredRepository(
            string.IsNullOrWhiteSpace(filter.RepositoryName) ? filter.DisplayName ?? string.Empty : filter.RepositoryName,
            Identity(filter.CanonicalSourceRef));
    }

    private static string? Identity(CanonicalSourceReferenceDto? canonicalSourceRef)
    {
        return string.IsNullOrWhiteSpace(canonicalSourceRef?.Value) ? null : canonicalSourceRef.Value;
    }
}

/// <summary>Which kind of configuration declared a coverage, recorded so a resolution can be explained.</summary>
internal enum CoverageSource
{
    CrawlConfiguration,
    WebhookConfiguration,
}
