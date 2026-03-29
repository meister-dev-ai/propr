using System.Diagnostics;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace MeisterProPR.Infrastructure.AzureDevOps;

/// <summary>
///     ADO-backed implementation of <see cref="IPrStatusFetcher" />.
///     Fetches the live status of a single pull request. All network and API errors are swallowed
///     and treated as <see cref="PrStatus.Active" /> (fail-safe: prefer no false cancellations).
/// </summary>
public sealed partial class AdoPrStatusFetcher(
    VssConnectionFactory connectionFactory,
    IClientAdoCredentialRepository credentialRepository,
    ILogger<AdoPrStatusFetcher> logger) : IPrStatusFetcher
{
    private static readonly ActivitySource ActivitySource = new("MeisterProPR.Infrastructure");

    /// <summary>
    ///     Override hook for unit tests. When set, this delegate is called instead of resolving
    ///     a <see cref="GitHttpClient" /> via <see cref="VssConnectionFactory" />.
    /// </summary>
    internal Func<string, CancellationToken, Task<GitHttpClient>>? GitClientResolver { get; set; }

    /// <inheritdoc />
    public async Task<PrStatus> GetStatusAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid? clientId,
        CancellationToken ct = default)
    {
        try
        {
            using var activity = ActivitySource.StartActivity("AdoPrStatusFetcher.GetStatus");
            activity?.SetTag("ado.organization_url", organizationUrl);
            activity?.SetTag("ado.project_id", projectId);
            activity?.SetTag("ado.repository_id", repositoryId);
            activity?.SetTag("ado.pull_request_id", pullRequestId);

            var credentials = clientId.HasValue
                ? await credentialRepository.GetByClientIdAsync(clientId.Value, ct)
                : null;

            GitHttpClient gitClient;
            if (this.GitClientResolver is not null)
            {
                gitClient = await this.GitClientResolver(organizationUrl, ct);
            }
            else
            {
                var connection = await connectionFactory.GetConnectionAsync(organizationUrl, credentials, ct);
                gitClient = connection.GetClient<GitHttpClient>();
            }

            var pr = await gitClient.GetPullRequestAsync(
                projectId,
                repositoryId,
                pullRequestId,
                cancellationToken: ct);

            return pr.Status switch
            {
                PullRequestStatus.Abandoned => PrStatus.Abandoned,
                PullRequestStatus.Completed => PrStatus.Completed,
                _ => PrStatus.Active,
            };
        }
        catch (Exception ex)
        {
            LogStatusFetchFailed(logger, pullRequestId, ex);
            return PrStatus.Active;
        }
    }
}
