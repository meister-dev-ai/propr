// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Support;

internal static class AdoProviderAdapterHelpers
{
    public static async Task<IReadOnlyList<ClientScmScopeDto>> ResolveOrganizationScopesAsync(
        IClientScmConnectionRepository connectionRepository,
        IClientScmScopeRepository scopeRepository,
        Guid clientId,
        ProviderHostRef host,
        CancellationToken ct)
    {
        EnsureAzureDevOps(host);

        var connections = await connectionRepository.GetByClientIdAsync(clientId, ct);
        var matchingConnections = connections
            .Where(connection => connection.ProviderFamily == ScmProvider.AzureDevOps)
            .Where(connection => string.Equals(
                connection.HostBaseUrl,
                host.HostBaseUrl,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(connection => connection.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var scopes = new List<ClientScmScopeDto>();
        foreach (var connection in matchingConnections)
        {
            var connectionScopes = await scopeRepository.GetByConnectionIdAsync(clientId, connection.Id, ct);
            scopes.AddRange(
                connectionScopes.Where(scope =>
                    string.Equals(scope.ScopeType, "organization", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        NormalizeHostBaseUrl(scope.ScopePath),
                        host.HostBaseUrl,
                        StringComparison.OrdinalIgnoreCase)));
        }

        return scopes
            .OrderBy(scope => scope.ScopePath, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    public static async Task<ClientScmScopeDto?> ResolveOrganizationScopeByIdAsync(
        IClientScmConnectionRepository connectionRepository,
        IClientScmScopeRepository scopeRepository,
        Guid clientId,
        Guid scopeId,
        CancellationToken ct)
    {
        var connections = await connectionRepository.GetByClientIdAsync(clientId, ct);
        foreach (var connection in
                 connections.Where(connection => connection.ProviderFamily == ScmProvider.AzureDevOps))
        {
            var scope = await scopeRepository.GetByIdAsync(clientId, connection.Id, scopeId, ct);
            if (scope is not null && string.Equals(scope.ScopeType, "organization", StringComparison.OrdinalIgnoreCase))
            {
                return scope;
            }
        }

        return null;
    }

    public static async Task<IReadOnlyList<string>> ResolveOrganizationUrlsAsync(
        IClientScmConnectionRepository connectionRepository,
        IClientScmScopeRepository scopeRepository,
        Guid clientId,
        ProviderHostRef host,
        CancellationToken ct)
    {
        EnsureAzureDevOps(host);

        var organizationUrls = (await ResolveOrganizationScopesAsync(
                connectionRepository,
                scopeRepository,
                clientId,
                host,
                ct))
            .Where(scope => scope.IsEnabled)
            .Select(scope => NormalizeOrganizationUrl(scope.ScopePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (organizationUrls.Count == 0)
        {
            organizationUrls.Add(host.HostBaseUrl);
        }

        return organizationUrls.AsReadOnly();
    }

    public static async Task<GitHttpClient> ResolveGitClientAsync(
        VssConnectionFactory connectionFactory,
        IClientScmConnectionRepository connectionRepository,
        Func<string, CancellationToken, Task<GitHttpClient>>? gitClientResolver,
        Guid clientId,
        string organizationUrl,
        CancellationToken ct)
    {
        if (gitClientResolver is not null)
        {
            return await gitClientResolver(organizationUrl, ct);
        }

        var credentials = await ResolveCredentialsAsync(connectionRepository, clientId, organizationUrl, ct);
        var connection = await connectionFactory.GetConnectionAsync(organizationUrl, credentials, ct);
        return connection.GetClient<GitHttpClient>();
    }

    public static async Task<AdoServicePrincipalCredentials?> ResolveCredentialsAsync(
        IClientScmConnectionRepository connectionRepository,
        Guid clientId,
        string organizationUrl,
        CancellationToken ct)
    {
        var host = new ProviderHostRef(ScmProvider.AzureDevOps, organizationUrl);
        var connection = await connectionRepository.GetOperationalConnectionAsync(clientId, host, ct);
        return ToAdoCredentials(connection);
    }

    public static Task<AdoServicePrincipalCredentials?> ResolveCredentialsAsync(
        IClientScmConnectionRepository connectionRepository,
        Guid? clientId,
        string organizationUrl,
        CancellationToken ct)
    {
        return clientId.HasValue
            ? ResolveCredentialsAsync(connectionRepository, clientId.Value, organizationUrl, ct)
            : Task.FromResult<AdoServicePrincipalCredentials?>(null);
    }

    public static ClientAdoOrganizationScopeDto ToAdoOrganizationScopeDto(ClientScmScopeDto scope)
    {
        return new ClientAdoOrganizationScopeDto(
            scope.Id,
            scope.ClientId,
            NormalizeOrganizationUrl(scope.ScopePath),
            scope.DisplayName,
            scope.IsEnabled,
            MapVerificationStatus(scope.VerificationStatus),
            scope.LastVerifiedAt,
            scope.LastVerificationError,
            scope.CreatedAt,
            scope.UpdatedAt);
    }

    public static async Task<ReviewRevision?> GetLatestRevisionAsync(
        GitHttpClient gitClient,
        string projectId,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct)
    {
        var iterations = await gitClient.GetPullRequestIterationsAsync(
            projectId,
            repositoryId,
            pullRequestId,
            false,
            null,
            ct);

        var latestIterationId = iterations
            .Select(iteration => iteration.Id ?? 0)
            .DefaultIfEmpty(0)
            .Max();
        if (latestIterationId <= 0)
        {
            return null;
        }

        var latestIteration = await gitClient.GetPullRequestIterationAsync(
            projectId,
            repositoryId,
            pullRequestId,
            latestIterationId,
            cancellationToken: ct);

        var headSha = latestIteration.SourceRefCommit?.CommitId;
        var baseSha = latestIteration.CommonRefCommit?.CommitId;
        if (string.IsNullOrWhiteSpace(headSha) || string.IsNullOrWhiteSpace(baseSha))
        {
            return null;
        }

        var providerRevisionId = latestIterationId.ToString(CultureInfo.InvariantCulture);
        return new ReviewRevision(headSha, baseSha, baseSha, providerRevisionId, $"{baseSha}...{headSha}");
    }

    public static ReviewDiscoveryItemDto ToDiscoveryItem(
        RepositoryRef repository,
        GitPullRequest pullRequest,
        ReviewRevision? revision,
        ReviewerIdentity? requestedReviewer)
    {
        var review = new CodeReviewRef(
            repository,
            CodeReviewPlatformKind.PullRequest,
            pullRequest.PullRequestId.ToString(CultureInfo.InvariantCulture),
            pullRequest.PullRequestId);

        return new ReviewDiscoveryItemDto(
            ScmProvider.AzureDevOps,
            repository,
            review,
            MapReviewState(pullRequest),
            revision,
            requestedReviewer,
            pullRequest.Title ?? $"Pull Request #{pullRequest.PullRequestId}",
            pullRequest.Url,
            StripRefsHeads(pullRequest.SourceRefName),
            StripRefsHeads(pullRequest.TargetRefName));
    }

    public static ReviewerIdentity? SelectRequestedReviewer(ProviderHostRef host, GitPullRequest pullRequest)
    {
        return pullRequest.Reviewers?
            .Select(reviewer => ToReviewerIdentity(host, reviewer))
            .FirstOrDefault(reviewer => reviewer is not null);
    }

    public static ReviewerIdentity? ToReviewerIdentity(ProviderHostRef host, IdentityRefWithVote? reviewer)
    {
        if (reviewer is null || string.IsNullOrWhiteSpace(reviewer.Id))
        {
            return null;
        }

        var login = !string.IsNullOrWhiteSpace(reviewer.UniqueName)
            ? reviewer.UniqueName!
            : !string.IsNullOrWhiteSpace(reviewer.DisplayName)
                ? reviewer.DisplayName!
                : reviewer.Id;

        return new ReviewerIdentity(
            host,
            reviewer.Id,
            login,
            reviewer.DisplayName ?? login,
            false);
    }

    public static string ResolveProjectId(RepositoryRef repository)
    {
        if (!string.IsNullOrWhiteSpace(repository.OwnerOrNamespace))
        {
            return repository.OwnerOrNamespace;
        }

        return repository.ProjectPath;
    }

    public static CodeReviewState MapReviewState(GitPullRequest pullRequest)
    {
        return pullRequest.Status switch
        {
            PullRequestStatus.Completed => CodeReviewState.Merged,
            PullRequestStatus.Abandoned => CodeReviewState.Closed,
            _ => CodeReviewState.Open,
        };
    }

    public static string? StripRefsHeads(string? branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName))
        {
            return null;
        }

        const string prefix = "refs/heads/";
        return branchName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? branchName[prefix.Length..]
            : branchName.Trim();
    }

    public static void EnsureAzureDevOps(ProviderHostRef host)
    {
        if (host.Provider != ScmProvider.AzureDevOps)
        {
            throw new InvalidOperationException("This adapter only supports Azure DevOps provider references.");
        }
    }

    public static string NormalizeHostBaseUrl(string organizationUrl)
    {
        return new ProviderHostRef(ScmProvider.AzureDevOps, organizationUrl).HostBaseUrl;
    }

    public static string? TryReadHeader(IReadOnlyDictionary<string, string> headers, string headerName)
    {
        foreach (var header in headers)
        {
            if (string.Equals(header.Key, headerName, StringComparison.OrdinalIgnoreCase))
            {
                return header.Value;
            }
        }

        return null;
    }

    public static string? TryReadString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => string.IsNullOrWhiteSpace(current.GetString()) ? null : current.GetString()!.Trim(),
            JsonValueKind.Number => current.ToString(),
            _ => null,
        };
    }

    private static AdoServicePrincipalCredentials? ToAdoCredentials(ClientScmConnectionCredentialDto? connection)
    {
        if (connection is null
            || connection.ProviderFamily != ScmProvider.AzureDevOps
            || connection.AuthenticationKind != ScmAuthenticationKind.OAuthClientCredentials
            || string.IsNullOrWhiteSpace(connection.OAuthTenantId)
            || string.IsNullOrWhiteSpace(connection.OAuthClientId))
        {
            return null;
        }

        return new AdoServicePrincipalCredentials(
            connection.OAuthTenantId,
            connection.OAuthClientId,
            connection.Secret);
    }

    private static AdoOrganizationVerificationStatus MapVerificationStatus(string? verificationStatus)
    {
        return verificationStatus?.Trim().ToLowerInvariant() switch
        {
            "verified" => AdoOrganizationVerificationStatus.Verified,
            "failed" => AdoOrganizationVerificationStatus.Unreachable,
            "stale" => AdoOrganizationVerificationStatus.Stale,
            _ => AdoOrganizationVerificationStatus.Unknown,
        };
    }

    public static string NormalizeOrganizationUrl(string organizationUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationUrl);
        return organizationUrl.Trim().TrimEnd('/');
    }
}
