// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using static MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Support.AdoProviderAdapterHelpers;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

internal sealed class AdoCodeReviewPublicationService(
    IClientScmConnectionRepository connectionRepository,
    IClientScmScopeRepository scopeRepository,
    VssConnectionFactory connectionFactory,
    IAdoCommentPoster commentPoster) : ICodeReviewPublicationService
{
    internal Func<string, CancellationToken, Task<GitHttpClient>>? GitClientResolver { get; set; }

    public ScmProvider Provider => ScmProvider.AzureDevOps;

    public async Task<ReviewCommentPostingDiagnosticsDto> PublishReviewAsync(
        Guid clientId,
        CodeReviewRef review,
        ReviewRevision revision,
        ReviewResult result,
        ReviewerIdentity reviewer,
        CancellationToken ct = default)
    {
        EnsureAzureDevOps(review.Repository.Host);

        var projectId = ResolveProjectId(review.Repository);
        Exception? lastException = null;

        foreach (var organizationUrl in await ResolveOrganizationUrlsAsync(
                     connectionRepository,
                     scopeRepository,
                     clientId,
                     review.Repository.Host,
                     ct))
        {
            try
            {
                var iterationId = await this.ResolveIterationIdAsync(
                    clientId,
                    organizationUrl,
                    projectId,
                    review,
                    revision,
                    ct);

                return await commentPoster.PostAsync(
                    organizationUrl,
                    projectId,
                    review.Repository.ExternalRepositoryId,
                    review.Number,
                    iterationId,
                    result,
                    clientId,
                    cancellationToken: ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                lastException = ex;
            }
        }

        throw lastException ?? new InvalidOperationException(
            "Azure DevOps review publication could not resolve an enabled organization scope for the repository host.");
    }

    private async Task<int> ResolveIterationIdAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        CodeReviewRef review,
        ReviewRevision revision,
        CancellationToken ct)
    {
        if (int.TryParse(
                revision.ProviderRevisionId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var iterationId) && iterationId > 0)
        {
            return iterationId;
        }

        var gitClient = await ResolveGitClientAsync(
            connectionFactory,
            connectionRepository,
            this.GitClientResolver,
            clientId,
            organizationUrl,
            ct);
        var iterations = await gitClient.GetPullRequestIterationsAsync(
            projectId,
            review.Repository.ExternalRepositoryId,
            review.Number,
            false,
            null,
            ct);

        var latestIterationId = iterations
            .Select(iteration => iteration.Id ?? 0)
            .DefaultIfEmpty(0)
            .Max();

        if (latestIterationId > 0)
        {
            return latestIterationId;
        }

        throw new InvalidOperationException("Azure DevOps review publication could not resolve a pull request iteration.");
    }
}
