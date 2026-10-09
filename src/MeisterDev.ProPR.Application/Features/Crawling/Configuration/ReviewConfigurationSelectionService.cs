// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.ProCursor.Contracts.Sources;


namespace MeisterDev.ProPR.Application.Features.Crawling.Configuration;

/// <summary>Coordinates saved scopes and provider-native discovery for guided configuration.</summary>
public sealed class ReviewConfigurationSelectionService(
    IScmProviderRegistry registry,
    IClientScmConnectionRepository? connections = null,
    IClientScmScopeRepository? scopes = null) : IReviewConfigurationSelectionService
{
    public async Task<Guid> ResolveHistoricalConnectionAsync(Guid clientId, Guid savedScopeId, CancellationToken ct = default)
    {
        var matches = new List<Guid>();
        if (connections is not null && scopes is not null)
        {
            foreach (var connection in await connections.GetByClientIdAsync(clientId, ct).ConfigureAwait(false))
            {
                var scope = await scopes.GetByIdAsync(clientId, connection.Id, savedScopeId, ct).ConfigureAwait(false);
                if (scope is not null && scope.ClientId == clientId && scope.ConnectionId == connection.Id)
                {
                    matches.Add(connection.Id);
                }
            }
        }

        if (matches.Count == 0)
        {
            throw new KeyNotFoundException("The saved scope does not belong to this client.");
        }

        return matches.Count == 1 ? matches[0] : throw new InvalidOperationException("Select the connection that owns this saved scope.");
    }

    public async Task<ConnectionDiscoveryContext> GetConnectionContextAsync(Guid clientId, Guid connectionId, CancellationToken ct = default)
    {
        var connection = connections is null ? null : await connections.GetOperationalConnectionByIdAsync(clientId, connectionId, ct).ConfigureAwait(false);
        if (connection is null || !connection.IsActive || connection.Id != connectionId || connection.ClientId != clientId)
        {
            throw new InvalidOperationException("The selected connection is not active for this client.");
        }

        return new(clientId, connectionId, new(connection.ProviderFamily, connection.HostBaseUrl));
    }

    public ConnectionDiscoveryDescriptor GetDescriptor(ConnectionDiscoveryContext context) =>
        registry.GetRepositoryDiscoveryProvider(context.Host.Provider).Descriptor;

    public bool SupportsMentionConfiguration(ConnectionDiscoveryContext context) =>
        registry.SupportsActivePullRequestDiscovery(context.Host.Provider) && registry.SupportsReviewThreadReply(context.Host.Provider);

    public Task<IReadOnlyList<ConnectionDiscoveryScope>> GetScopesAsync(ConnectionDiscoveryContext context, CancellationToken ct = default) =>
        registry.GetRepositoryDiscoveryProvider(context.Host.Provider).ListScopesAsync(context, ct);

    private async Task<ClientScmScopeDto> GetSelectedSavedScopeAsync(ConnectionDiscoveryContext context, string scopeKey, CancellationToken ct)
    {
        var options = await this.GetScopesAsync(context, ct).ConfigureAwait(false);
        var selected = options.SingleOrDefault(option => option.ScopeKey == scopeKey);
        var scope = selected?.SavedScopeId is { } id && scopes is not null
            ? await scopes.GetByIdAsync(context.ClientId, context.ConnectionId, id, ct).ConfigureAwait(false)
            : null;
        if (scope is null || !scope.IsEnabled || scope.ClientId != context.ClientId || scope.ConnectionId != context.ConnectionId)
        {
            throw new InvalidOperationException("The selected scope does not belong to this connection.");
        }

        return scope;
    }

    public async Task<IReadOnlyList<ScmDiscoveryProjectOption>> GetProjectsAsync(
        ConnectionDiscoveryContext context, string scopeKey, CancellationToken ct = default)
    {
        if (this.GetDescriptor(context).ProjectLabel is null)
        {
            throw new NotSupportedException("Project discovery is not supported by this connection.");
        }

        var scope = await this.GetSelectedSavedScopeAsync(context, scopeKey, ct).ConfigureAwait(false);
        return await registry.GetProviderAdminDiscoveryService(context.Host.Provider)
            .ListProjectOptionsAsync(context.ClientId, scope.Id, ct, context.ConnectionId).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConnectionDiscoverySource>> GetSourcesAsync(
        ConnectionDiscoveryContext context, string scopeKey, string? projectId, ProCursorSourceKind kind, CancellationToken ct = default)
    {
        if (!this.GetDescriptor(context).SourceKinds.Any(source => source.Kind == kind))
        {
            throw new NotSupportedException("The selected source kind is not supported by this connection.");
        }

        var native = registry.GetRepositoryDiscoveryProvider(context.Host.Provider);
        var scope = (await native.ListScopesAsync(context, ct).ConfigureAwait(false)).SingleOrDefault(option => option.ScopeKey == scopeKey)
                    ?? throw new InvalidOperationException("The selected scope is not available through this connection.");
        var nativeProject = this.GetDescriptor(context).ProjectLabel is not null
            ? (await this.ResolveNativeProjectAsync(context, scope.ScopeKey, projectId, ct).ConfigureAwait(false)).ProjectId
            : projectId;
        return await native.ListSourcesAsync(context, scope.ScopeKey, nativeProject, kind, ct).ConfigureAwait(false);
    }

    private async Task<ScmDiscoveryProjectOption> ResolveNativeProjectAsync(
        ConnectionDiscoveryContext context, string scopeKey, string? projectId, CancellationToken ct)
    {
        var projects = await this.GetProjectsAsync(context, scopeKey, ct).ConfigureAwait(false);
        return projects.SingleOrDefault(project => string.Equals(project.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
               ?? projects.SingleOrDefault(project => string.Equals(project.ProjectName, projectId, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException("The selected project is not available in this scope.");
    }

    public async Task<IReadOnlyList<ScmDiscoveryBranchOption>> GetBranchesAsync(
        ConnectionDiscoveryContext context, string scopeKey, string projectId, ProCursorSourceKind kind,
        CanonicalSourceReferenceDto reference, CancellationToken ct = default)
    {
        if (!this.GetDescriptor(context).SupportsBranches)
        {
            throw new NotSupportedException("Branch discovery is not supported by this connection.");
        }

        var source = (await this.GetSourcesAsync(context, scopeKey, projectId, kind, ct).ConfigureAwait(false))
                     .SingleOrDefault(source => source.CanonicalSourceRef == reference)
                     ?? throw new InvalidOperationException("The selected source is no longer available.");
        return await registry.GetProviderAdminDiscoveryService(context.Host.Provider)
            .ListBranchOptionsAsync(context.ClientId, source.OrganizationScopeId!.Value, source.ProviderProjectKey, kind, reference, ct, context.ConnectionId)
            .ConfigureAwait(false);
    }

    public async Task<(ScmProvider Provider, Guid? OrganizationScopeId, string ProviderScopePath, string ProviderProjectKey)> ResolveConnectionSelectionAsync(
        Guid clientId, Guid connectionId, string scopeKey, string? projectId, ScmProvider? suppliedProvider,
        Guid? suppliedScopeId, string? suppliedScopePath, CancellationToken ct = default)
    {
        var context = await this.GetConnectionContextAsync(clientId, connectionId, ct).ConfigureAwait(false);
        if (suppliedProvider.HasValue && suppliedProvider != context.Host.Provider)
        {
            throw new InvalidOperationException("The supplied provider conflicts with the selected connection.");
        }

        var scope = (await this.GetScopesAsync(context, ct).ConfigureAwait(false)).SingleOrDefault(option => option.ScopeKey == scopeKey)
                    ?? throw new InvalidOperationException("The selected scope is not available.");
        if (this.GetDescriptor(context).ProjectLabel is not null)
        {
            await this.ResolveNativeProjectAsync(context, scopeKey, projectId, ct).ConfigureAwait(false);
        }

        var coordinates = registry.GetRepositoryDiscoveryProvider(context.Host.Provider).GetConfigurationCoordinates(context, scope, projectId);
        if (suppliedScopeId.HasValue && suppliedScopeId != coordinates.OrganizationScopeId ||
            !string.IsNullOrWhiteSpace(suppliedScopePath) && !string.Equals(
                suppliedScopePath, coordinates.ProviderScopePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The supplied scope coordinates conflict with the selected connection.");
        }

        return (context.Host.Provider, coordinates.OrganizationScopeId, coordinates.ProviderScopePath, coordinates.ProviderProjectKey);
    }

    public async Task<IReadOnlyList<CrawlRepoFilterDto>> ResolveConnectionFiltersAsync(
        ConnectionDiscoveryContext context, string scopeKey, string? projectId, IReadOnlyList<CrawlRepoFilterDto>? filters, CancellationToken ct = default)
    {
        if (filters is null || filters.Count == 0)
        {
            return [];
        }

        var sources = await this.GetSourcesAsync(context, scopeKey, projectId, ProCursorSourceKind.Repository, ct).ConfigureAwait(false);
        return filters.Select(filter =>
        {
            var source = sources.SingleOrDefault(source => source.CanonicalSourceRef == filter.CanonicalSourceRef)
                         ?? throw new InvalidOperationException("A selected repository is no longer available through this connection.");
            return filter with
            {
                Id = Guid.Empty, RepositoryName = source.DisplayName, DisplayName = source.DisplayName,
                CanonicalSourceRef = source.CanonicalSourceRef, TargetBranchPatterns = NormalizeBranchPatterns(filter.TargetBranchPatterns)
            };
        }).ToList();
    }

    public bool HasScopeSelection(ScmProvider provider, Guid? scopeId, string? path) =>
        !string.IsNullOrWhiteSpace(path) || scopeId.HasValue && this.RequiresOrganizationScope(provider);

    public bool HasScopeSelection(WebhookProviderType provider, Guid? scopeId, string? path) =>
        !string.IsNullOrWhiteSpace(path) ||
        registry.CompatibilityCodec.TryFromWebhook(provider, out var mapped) && this.HasScopeSelection(mapped, scopeId, path);

    private bool RequiresOrganizationScope(ScmProvider? provider) =>
        provider is { } value &&
        (Enum.IsDefined(value)
            ? registry.GetReviewSourcePolicy(value)
            : registry.GetSourceIdentityPolicy(value)).RequiresOrganizationScope;

    private ScmProvider? MapWebhookForSelection(WebhookProviderType provider) =>
        registry.CompatibilityCodec.TryFromWebhook(provider, out var mapped) ? mapped : null;

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string> NormalizeBranchPatterns(IReadOnlyList<string>? targetBranchPatterns)
    {
        return (targetBranchPatterns ?? [])
            .Select(pattern => pattern.Trim())
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    private static string ResolveRepositoryName(CrawlRepoFilterDto filter)
    {
        var repositoryName = NormalizeOptional(filter.RepositoryName)
                             ?? NormalizeOptional(filter.DisplayName)
                             ?? NormalizeOptional(filter.CanonicalSourceRef?.Value);

        return repositoryName
               ?? throw new InvalidOperationException("Each repository filter must include a repository name, display name, or canonical source reference.");
    }

    public Task<(Guid? OrganizationScopeId, string OrganizationUrl)> ResolveScopeAsync(
        Guid clientId, ScmProvider provider, Guid? organizationScopeId, string? organizationUrl, CancellationToken ct, string configurationKind = "crawl") =>
        this.ResolveScopeCoreAsync(clientId, provider, organizationScopeId, organizationUrl, ct, configurationKind);

    public Task<(Guid? OrganizationScopeId, string OrganizationUrl)> ResolveScopeAsync(
        Guid clientId, WebhookProviderType provider, Guid? organizationScopeId, string? organizationUrl, CancellationToken ct,
        string configurationKind = "webhook") =>
        this.ResolveScopeCoreAsync(clientId, this.MapWebhookForSelection(provider), organizationScopeId, organizationUrl, ct, configurationKind);

    private async Task<(Guid? OrganizationScopeId, string OrganizationUrl)> ResolveScopeCoreAsync(
        Guid clientId,
        ScmProvider? provider,
        Guid? organizationScopeId,
        string? organizationUrl,
        CancellationToken ct, string configurationKind = "crawl")
    {
        if (!this.RequiresOrganizationScope(provider))
        {
            var sourcePolicy = registry.GetSourceIdentityPolicy(provider.GetValueOrDefault((ScmProvider)(-1)));
            return (null, sourcePolicy.ResolveGuidedScopePath(organizationUrl, configurationKind));
        }

        if (organizationScopeId.HasValue)
        {
            var capturedScope = await registry.GetProviderAdminDiscoveryService(provider.GetValueOrDefault())
                .GetScopeAsync(clientId, organizationScopeId.Value, ct);
            var scope = registry.GetSourceIdentityPolicy(provider.GetValueOrDefault())
                .ValidateGuidedSelectedScope(capturedScope);
            return (scope.Id, scope.ScopePath);
        }

        return (null, registry.GetSourceIdentityPolicy(provider.GetValueOrDefault())
            .ResolveGuidedScopePath(organizationUrl, configurationKind));
    }

    public Task<IReadOnlyList<CrawlRepoFilterDto>> ResolveFiltersAsync(
        Guid clientId, ScmProvider provider, Guid? organizationScopeId, string projectId, IReadOnlyList<CrawlRepoFilterDto>? repoFilters,
        CancellationToken ct, string configurationKind = "crawl") =>
        this.ResolveFiltersCoreAsync(clientId, provider, organizationScopeId, projectId, repoFilters, ct, configurationKind);

    public Task<IReadOnlyList<CrawlRepoFilterDto>> ResolveFiltersAsync(
        Guid clientId, WebhookProviderType provider, Guid? organizationScopeId, string projectId, IReadOnlyList<CrawlRepoFilterDto>? repoFilters,
        CancellationToken ct, string configurationKind = "webhook") =>
        this.ResolveFiltersCoreAsync(clientId, this.MapWebhookForSelection(provider), organizationScopeId, projectId, repoFilters, ct, configurationKind);

    private async Task<IReadOnlyList<CrawlRepoFilterDto>> ResolveFiltersCoreAsync(
        Guid clientId,
        ScmProvider? provider,
        Guid? organizationScopeId,
        string projectId,
        IReadOnlyList<CrawlRepoFilterDto>? repoFilters,
        CancellationToken ct, string configurationKind = "crawl")
    {
        if (repoFilters is null)
        {
            return [];
        }

        var filterDtos = new List<CrawlRepoFilterDto>(repoFilters.Count);
        IReadOnlyList<ScmDiscoveryCrawlFilterOption> availableFilters = [];

        var organizationScoped = organizationScopeId.HasValue && this.RequiresOrganizationScope(provider);
        if (organizationScoped && organizationScopeId is { } selectedScopeId)
        {
            availableFilters = await registry.GetProviderAdminDiscoveryService(provider.GetValueOrDefault())
                .ListCrawlFilterOptionsAsync(clientId, selectedScopeId, projectId, ct);
        }

        foreach (var filter in repoFilters)
        {
            var canonicalSourceRef = filter.CanonicalSourceRef;
            var normalizedProvider = NormalizeOptional(canonicalSourceRef?.Provider);
            var normalizedValue = NormalizeOptional(canonicalSourceRef?.Value);
            var normalizedDisplayName = NormalizeOptional(filter.DisplayName);
            var repositoryName = ResolveRepositoryName(filter);
            var targetBranchPatterns = NormalizeBranchPatterns(filter.TargetBranchPatterns);

            if (organizationScoped && normalizedProvider is not null &&
                normalizedValue is not null)
            {
                var matchedFilter = availableFilters.FirstOrDefault(option =>
                    string.Equals(
                        option.CanonicalSourceRef.Provider,
                        normalizedProvider,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        option.CanonicalSourceRef.Value,
                        normalizedValue,
                        StringComparison.OrdinalIgnoreCase));

                if (matchedFilter is null)
                {
                    throw new InvalidOperationException(
                        registry.GetSourceIdentityPolicy(provider.GetValueOrDefault())
                            .GetGuidedFilterUnavailableMessage(configurationKind, normalizedDisplayName ?? repositoryName));
                }

                filterDtos.Add(
                    new CrawlRepoFilterDto(
                        Guid.Empty,
                        repositoryName,
                        targetBranchPatterns,
                        new CanonicalSourceReferenceDto(normalizedProvider, normalizedValue),
                        normalizedDisplayName ?? matchedFilter.DisplayName));
                continue;
            }

            filterDtos.Add(
                new CrawlRepoFilterDto(
                    Guid.Empty,
                    repositoryName,
                    targetBranchPatterns,
                    normalizedProvider is not null && normalizedValue is not null
                        ? new CanonicalSourceReferenceDto(normalizedProvider, normalizedValue)
                        : null,
                    normalizedDisplayName));
        }

        return filterDtos.AsReadOnly();
    }

    public async Task<GuidedSourceSelection> ResolveGuidedSourceAsync(
        Guid clientId, ScmProvider provider, Guid scopeId, string project, ProCursorSourceKind kind, CanonicalSourceReferenceDto reference,
        CancellationToken ct) =>
        await registry.GetProviderAdminDiscoveryService(provider)
            .ResolveGuidedSourceAsync(
                clientId, scopeId, project, kind,
                reference is null ? null : new(reference.Provider, reference.Value), ct);
}
