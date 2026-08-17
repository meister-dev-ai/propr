// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Security;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;

/// <summary>
///     Forgejo implementation of <see cref="IActivePullRequestDiscoveryProvider" />, against its
///     Gitea-compatible API.
/// </summary>
/// <remarks>
///     Every Forgejo instance is self-hosted, so there is no host to fall back on: the address comes from the
///     client's connection and nothing is asked without one.
///     The listing has no "updated since" filter, so it is requested sorted by recent update and the read
///     stops at the first page containing nothing newer than the watermark. The check is per page rather than
///     per entry because Forgejo versions differ in how they order the listing.
/// </remarks>
internal sealed class ForgejoActivePrFetcher(
    ForgejoConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory,
    ILogger<ForgejoActivePrFetcher> logger)
    : ActivePullRequestDiscoveryProviderBase<ForgejoConnectionVerifier.ForgejoConnectionContext>(logger)
{
    private const int PageSize = 50;

    /// <summary>
    ///     How many pages one repository is read across. The bound exists so an instance that ignores the page
    ///     parameter is not an endless loop.
    /// </summary>
    private const int MaxPages = 20;

    /// <inheritdoc />
    public override ScmProvider Provider => ScmProvider.Forgejo;

    /// <inheritdoc />
    protected override async Task<ForgejoConnectionVerifier.ForgejoConnectionContext> PrepareAsync(
        ActivePullRequestQuery query,
        CancellationToken ct)
    {
        // There is no default Forgejo host. Without one there is nowhere to send a request, so none is sent.
        if (string.IsNullOrWhiteSpace(query.ScopePath))
        {
            throw new InvalidOperationException("A Forgejo mention configuration needs the host from its client connection; none is configured.");
        }

        try
        {
            return await connectionVerifier.VerifyAsync(query.ClientId, HostOf(query), ct);
        }
        catch (HttpRequestException ex) when (HasTlsCause(ex))
        {
            throw UntrustedCertificate(query.ScopePath, ex);
        }
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<ActivePullRequestRef>> ListRepositoryAsync(
        ForgejoConnectionVerifier.ForgejoConnectionContext context,
        ActivePullRequestQuery query,
        ClaimedRepositoryRef repository,
        CancellationToken ct)
    {
        var host = HostOf(query);
        var repositoryPath = await this.ResolveRepositoryPathAsync(context, host, repository, ct);
        var owner = repositoryPath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                    ?? string.Empty;
        var refs = new List<ActivePullRequestRef>();
        var reachedPageLimit = true;

        for (var page = 1; page <= MaxPages; page++)
        {
            var listingQuery = string.Create(
                CultureInfo.InvariantCulture,
                $"state=open&sort=recentupdate&limit={PageSize}&page={page}");

            using var response = await this.SendAsync(
                context,
                ForgejoConnectionVerifier.BuildApiUri(host, $"/repos/{repositoryPath}/pulls", listingQuery),
                ct);

            if (ProviderThrottleSignal.IsThrottled(response))
            {
                throw new ProviderThrottledException($"Forgejo throttled the pull-request listing for {repositoryPath}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Forgejo pull-request listing for {repositoryPath} failed with status {(int)response.StatusCode}.");
            }

            var pullRequests =
                await response.Content.ReadFromJsonAsync<IReadOnlyList<ForgejoPullRequestSummary>>(ct) ?? [];

            if (pullRequests.Count == 0)
            {
                reachedPageLimit = false;
                break;
            }

            var newOnThisPage = 0;
            foreach (var pullRequest in pullRequests)
            {
                var updatedAt = pullRequest.UpdatedAt ?? pullRequest.CreatedAt;
                if (updatedAt is null || updatedAt <= query.UpdatedAfter)
                {
                    continue;
                }

                newOnThisPage++;
                refs.Add(
                    new ActivePullRequestRef(
                        query.ScopePath,
                        owner,

                        // The claimed identifier, so the scan matches what it reads back against the claim
                        // whether the configuration stored a numeric id or an owner/name path.
                        repository.RepositoryId,
                        pullRequest.Number,
                        updatedAt.Value));
            }

            // A whole page with nothing newer ends the read. Judging the page rather than the first entry on
            // it leaves room for an instance whose ordering differs from the one asked for.
            if (newOnThisPage == 0 || pullRequests.Count < PageSize)
            {
                reachedPageLimit = false;
                break;
            }
        }

        if (reachedPageLimit)
        {
            ActivePullRequestDiscoveryLog.PageLimitReached(logger, this.Provider, repositoryPath, MaxPages);
        }

        return refs;
    }

    private static ProviderHostRef HostOf(ActivePullRequestQuery query)
    {
        return new ProviderHostRef(ScmProvider.Forgejo, query.ScopePath);
    }

    /// <summary>
    ///     Turns what the configuration stored into the <c>owner/name</c> pair the Gitea-compatible API is
    ///     addressed by.
    /// </summary>
    private async Task<string> ResolveRepositoryPathAsync(
        ForgejoConnectionVerifier.ForgejoConnectionContext context,
        ProviderHostRef host,
        ClaimedRepositoryRef repository,
        CancellationToken ct)
    {
        if (repository.RepositoryId.Contains('/', StringComparison.Ordinal))
        {
            return repository.RepositoryId.Trim();
        }

        using var response = await this.SendAsync(
            context,
            ForgejoConnectionVerifier.BuildApiUri(
                host,
                $"/repositories/{Uri.EscapeDataString(repository.RepositoryId)}"),
            ct);

        if (ProviderThrottleSignal.IsThrottled(response))
        {
            throw new ProviderThrottledException($"Forgejo throttled the repository lookup for {repository.RepositoryId}.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Forgejo repository lookup for {repository.RepositoryId} failed with status {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<ForgejoRepositorySummary>(ct);
        if (string.IsNullOrWhiteSpace(payload?.FullName))
        {
            throw new InvalidOperationException($"Forgejo repository lookup for {repository.RepositoryId} returned no repository name.");
        }

        return payload.FullName.Trim();
    }

    private async Task<HttpResponseMessage> SendAsync(
        ForgejoConnectionVerifier.ForgejoConnectionContext context,
        Uri uri,
        CancellationToken ct)
    {
        using var request = ForgejoConnectionVerifier.CreateAuthenticatedRequest(uri, context.Connection.Secret);

        try
        {
            return await httpClientFactory.CreateClient("ForgejoProvider").SendAsync(request, ct);
        }
        catch (HttpRequestException ex) when (HasTlsCause(ex))
        {
            throw UntrustedCertificate(uri.GetLeftPart(UriPartial.Authority), ex);
        }
    }

    /// <summary>
    ///     Names the certificate as the reason. A self-hosted instance behind one this installation does not
    ///     trust otherwise surfaces as a bare transport failure, which sends an operator looking at the network.
    /// </summary>
    private static InvalidOperationException UntrustedCertificate(string address, Exception cause)
    {
        return new InvalidOperationException(
            $"Forgejo at {address} presented a certificate this installation does not trust. Install the instance's certificate authority, or use a certificate from one already trusted.",
            cause);
    }

    private static bool HasTlsCause(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException)
            {
                return true;
            }
        }

        return false;
    }

    private sealed record ForgejoPullRequestSummary(
        [property: JsonPropertyName("number")] int Number,
        [property: JsonPropertyName("updated_at")]
        DateTimeOffset? UpdatedAt,
        [property: JsonPropertyName("created_at")]
        DateTimeOffset? CreatedAt);

    private sealed record ForgejoRepositorySummary(
        [property: JsonPropertyName("full_name")]
        string? FullName);
}
