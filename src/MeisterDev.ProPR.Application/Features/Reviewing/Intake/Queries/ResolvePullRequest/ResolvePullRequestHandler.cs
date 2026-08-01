// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Intake.Queries.ResolvePullRequest;

/// <summary>
///     Answers which client covers a pull request, and under which coordinates, from what a pull request's
///     web address reveals.
/// </summary>
/// <remarks>
///     <para>
///         Every review-scoped endpoint is addressed by scope path, project key, repository identity, and
///         number. An address supplies a host, an owner segment, a repository <em>name</em>, and a number —
///         so two of the four are missing, and for Azure DevOps and Forgejo the missing two are opaque
///         identifiers that appear nowhere in the address.
///     </para>
///     <para>
///         Configuration supplies most of it. A repository reaches ProPR either through a crawl
///         configuration or through a webhook configuration, and either is sufficient: both record the scope
///         path and project key verbatim as the review pipeline uses them, plus the repositories they cover
///         by name. Those two values are returned rather than reconstructed, because they are stored
///         settings — Forgejo keeps the host in the scope path while Azure DevOps keeps host plus
///         organization, so any derivation would disagree with what a review job carries for one of them.
///     </para>
///     <para>
///         What configuration often lacks is the repository identity: a webhook is registered by name, so it
///         has no reason to record one. That single value is filled in from the provider's repository
///         discovery — the same adapter guided configuration uses. Only the identity comes from discovery,
///         never the scope path or project key, which keeps the authoritative values authoritative and
///         confines the network call to the one thing it is needed for.
///     </para>
/// </remarks>
public sealed partial class ResolvePullRequestHandler(
    ICrawlConfigurationRepository crawlConfigurationRepository,
    IWebhookConfigurationRepository webhookConfigurationRepository,
    IScmProviderRegistry providerRegistry,
    ILogger<ResolvePullRequestHandler> logger)
{
    private static readonly ResolvePullRequestResultDto Empty = new([]);

    /// <summary>Resolves every client that covers the addressed pull request.</summary>
    /// <param name="query">The address components to resolve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    ///     Matches ordered so that addressable ones come first. An empty result means no client covers the
    ///     repository, which is a normal answer rather than a failure.
    /// </returns>
    public async Task<ResolvePullRequestResultDto> HandleAsync(
        ResolvePullRequestQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var requestedAuthority = TryReadAuthority(query.HostBaseUrl);
        if (requestedAuthority is null ||
            string.IsNullOrWhiteSpace(query.RepositoryName) ||
            query.PullRequestNumber < 1)
        {
            return Empty;
        }

        // An empty set means the caller can see nothing, which is distinct from null (a platform
        // administrator seeing everything). Reading configuration for it would be pointless work.
        if (query.AccessibleClientIds is { Count: 0 })
        {
            return Empty;
        }

        var coverages = await this.LoadCoveragesAsync(query.AccessibleClientIds, cancellationToken);
        var visibleClientIds = query.AccessibleClientIds is null
            ? null
            : new HashSet<Guid>(query.AccessibleClientIds);

        var scope = query.ScopePath?.Trim().Trim('/') ?? string.Empty;
        var matches = new List<ResolvedPullRequestDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var coverage in coverages)
        {
            // Filter on the way out as well as scoping the read, so a repository that over-returns cannot
            // widen what this caller sees.
            if (visibleClientIds is not null && !visibleClientIds.Contains(coverage.ClientId))
            {
                continue;
            }

            if (!CoversHost(coverage, requestedAuthority) || !CoversScope(coverage, scope))
            {
                continue;
            }

            var covered = FindRepository(coverage, query.RepositoryName);

            // No named repositories at all means the configuration covers its whole scope, so it covers
            // this one without naming it. Named repositories that exclude this one mean it does not.
            if (covered is null && coverage.CoveredRepositories.Count > 0)
            {
                continue;
            }

            var repositoryId = covered?.ExternalRepositoryId
                               ?? await this.DiscoverRepositoryIdAsync(
                                   coverage,
                                   query.RepositoryName,
                                   cancellationToken);

            var match = new ResolvedPullRequestDto(
                coverage.ClientId,
                coverage.Provider,
                coverage.ProviderScopePath,
                coverage.ProviderProjectKey,
                repositoryId,
                covered?.Name,
                query.PullRequestNumber,
                coverage.IsActive);

            // A repository can be covered twice — by a crawl configuration and by a webhook, say. They
            // resolve to one set of coordinates, so they are one answer.
            var identity = string.Join(
                '|',
                match.ClientId,
                match.ProviderScopePath,
                match.ProviderProjectKey,
                match.RepositoryId ?? string.Empty);

            if (seen.Add(identity))
            {
                matches.Add(match);
            }
        }

        return new ResolvePullRequestResultDto(
            matches
                .OrderByDescending(match => match.RepositoryId is not null)
                .ThenByDescending(match => match.IsActiveConfiguration)
                .ToList()
                .AsReadOnly());
    }

    private async Task<List<PullRequestCoverage>> LoadCoveragesAsync(
        IReadOnlyCollection<Guid>? accessibleClientIds,
        CancellationToken cancellationToken)
    {
        var crawlConfigurations = accessibleClientIds is null
            ? await crawlConfigurationRepository.GetAllAsync(cancellationToken)
            : await crawlConfigurationRepository.GetByClientIdsAsync(accessibleClientIds, cancellationToken);

        var webhookConfigurations = accessibleClientIds is null
            ? await webhookConfigurationRepository.GetAllAsync(cancellationToken)
            : await webhookConfigurationRepository.GetByClientIdsAsync(accessibleClientIds, cancellationToken);

        var coverages = new List<PullRequestCoverage>(crawlConfigurations.Count + webhookConfigurations.Count);
        coverages.AddRange(crawlConfigurations.Select(PullRequestCoverage.FromCrawlConfiguration));
        coverages.AddRange(webhookConfigurations.Select(PullRequestCoverage.FromWebhookConfiguration));

        return coverages;
    }

    /// <summary>
    ///     Fills in the one value configuration usually lacks. Only the identity is taken from discovery; the
    ///     scope path and project key stay the configured ones.
    /// </summary>
    /// <returns>
    ///     The provider repository identity, or <see langword="null" /> when discovery cannot supply one — in
    ///     which case the caller learns the repository is covered and not yet addressable, which is a state
    ///     it can act on.
    /// </returns>
    private async Task<string?> DiscoverRepositoryIdAsync(
        PullRequestCoverage coverage,
        string repositoryName,
        CancellationToken cancellationToken)
    {
        if (!providerRegistry.IsRegistered(coverage.Provider))
        {
            return null;
        }

        try
        {
            var host = new ProviderHostRef(coverage.Provider, coverage.ProviderScopePath);
            var discovery = providerRegistry.GetRepositoryDiscoveryProvider(coverage.Provider);
            var repositories = await discovery.ListRepositoriesAsync(
                coverage.ClientId,
                host,
                coverage.ProviderProjectKey,
                cancellationToken);

            var wanted = repositoryName.Trim();
            var found = repositories.FirstOrDefault(repository =>
                string.Equals(repository.RepositoryName, wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(LastSegment(repository.ProjectPath), wanted, StringComparison.OrdinalIgnoreCase));

            return found?.ExternalRepositoryId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Discovery reaches the provider over the network with the client's credential, so it can fail
            // for reasons that have nothing to do with this request. Degrading to a covered-but-not-
            // addressable answer keeps the rest of the resolution useful.
            LogDiscoveryFailed(logger, coverage.ClientId, coverage.Provider.ToString(), repositoryName, ex);
            return null;
        }
    }

    private static bool CoversHost(PullRequestCoverage coverage, string requestedAuthority)
    {
        var configuredAuthority = TryReadAuthority(coverage.ProviderScopePath);
        return configuredAuthority is not null
               && string.Equals(configuredAuthority, requestedAuthority, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Matches the address's owner segment against configuration, which splits that identity by
    ///     provider: Azure DevOps puts the organization in the scope path, while Forgejo, GitHub, and GitLab
    ///     keep only the host there and put the owner in the project key.
    /// </summary>
    private static bool CoversScope(PullRequestCoverage coverage, string scope)
    {
        if (scope.Length == 0)
        {
            return true;
        }

        if (string.Equals(coverage.ProviderProjectKey, scope, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // When the scope path names only a host the owner lives in the project key, which was already
        // compared above. Accepting the configuration here would match a different owner on the same host.
        var configuredPath = TryReadPath(coverage.ProviderScopePath);

        return string.Equals(configuredPath, scope, StringComparison.OrdinalIgnoreCase)
               || configuredPath.EndsWith('/' + scope, StringComparison.OrdinalIgnoreCase);
    }

    private static CoveredRepository? FindRepository(PullRequestCoverage coverage, string repositoryName)
    {
        var wanted = repositoryName.Trim();

        return coverage.CoveredRepositories.FirstOrDefault(repository =>
            string.Equals(repository.Name, wanted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(LastSegment(repository.Name), wanted, StringComparison.OrdinalIgnoreCase));
    }

    private static string LastSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim().TrimEnd('/');
        var separator = trimmed.LastIndexOf('/');
        return separator < 0 ? trimmed : trimmed[(separator + 1)..];
    }

    private static string? TryReadAuthority(string? value)
    {
        return Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && uri.IsAbsoluteUri
            ? uri.GetLeftPart(UriPartial.Authority).TrimEnd('/')
            : null;
    }

    private static string TryReadPath(string? value)
    {
        return Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            ? Uri.UnescapeDataString(uri.AbsolutePath).Trim('/')
            : string.Empty;
    }

    [LoggerMessage(
        EventId = 6301,
        Level = LogLevel.Debug,
        Message =
            "Repository discovery could not supply an identity for {RepositoryName} on {Provider} for client {ClientId}; resolving as covered but not addressable.")]
    private static partial void LogDiscoveryFailed(
        ILogger logger,
        Guid clientId,
        string provider,
        string repositoryName,
        Exception exception);
}
