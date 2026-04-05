// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using MeisterProPR.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace MeisterProPR.Infrastructure.AzureDevOps;

/// <summary>
///     ADO implementation of <see cref="IAdoThreadClient" />.
///     Updates pull request thread status using the ADO Git REST API PATCH endpoint.
/// </summary>
internal sealed partial class AdoThreadClient(
    VssConnectionFactory connectionFactory,
    IClientAdoCredentialRepository credentialRepository,
    ILogger<AdoThreadClient> logger) : IAdoThreadClient
{
    private static readonly ActivitySource ActivitySource = new("MeisterProPR.Infrastructure");

    /// <summary>
    ///     Exposed for testing: allows injection of a <see cref="GitHttpClient" /> without a real VssConnection.
    /// </summary>
    internal Func<string, CancellationToken, Task<GitHttpClient>>? GitClientResolver { get; set; }

    /// <inheritdoc />
    public async Task UpdateThreadStatusAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int threadId,
        string status,
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("AdoThreadClient.UpdateThreadStatus");
        activity?.SetTag("ado.organization_url", organizationUrl);
        activity?.SetTag("ado.pull_request_id", pullRequestId);
        activity?.SetTag("ado.thread_id", threadId);
        activity?.SetTag("ado.status", status);

        var commentStatus = Enum.TryParse<CommentThreadStatus>(status, true, out var parsed)
            ? parsed
            : CommentThreadStatus.Unknown;

        var thread = new GitPullRequestCommentThread { Status = commentStatus };

        var gitClient = await this.ResolveGitClientAsync(organizationUrl, clientId, cancellationToken);

        await gitClient.UpdateThreadAsync(
            thread,
            projectId,
            repositoryId,
            pullRequestId,
            threadId,
            null,
            cancellationToken);

        LogStatusUpdated(logger, organizationUrl, pullRequestId, threadId, status);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "AdoThreadClient: set thread {ThreadId} on PR#{PullRequestId} in {OrganizationUrl} to status '{Status}'")]
    private static partial void LogStatusUpdated(
        ILogger logger,
        string organizationUrl,
        int pullRequestId,
        int threadId,
        string status);

    private async Task<GitHttpClient> ResolveGitClientAsync(
        string organizationUrl,
        Guid? clientId,
        CancellationToken ct)
    {
        if (this.GitClientResolver is not null)
        {
            return await this.GitClientResolver(organizationUrl, ct);
        }

        var credentials = clientId.HasValue
            ? await credentialRepository.GetByClientIdAsync(clientId.Value, ct)
            : null;

        var connection = await connectionFactory.GetConnectionAsync(organizationUrl, credentials, ct);
        await connection.ConnectAsync(cancellationToken: ct);
        return connection.GetClient<GitHttpClient>();
    }
}
