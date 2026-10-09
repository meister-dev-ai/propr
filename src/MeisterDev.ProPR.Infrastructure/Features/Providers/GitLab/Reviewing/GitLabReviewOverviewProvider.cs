// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Support;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Security;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Reviewing;

internal sealed class GitLabReviewOverviewProvider(GitLabConnectionVerifier connectionVerifier, IHttpClientFactory httpClientFactory) : IReviewOverviewProvider
{
    public ScmProvider Provider => ScmProvider.GitLab;

    public async Task<ReviewOverviewDto> GetOverviewAsync(Guid clientId, CodeReviewRef review, ReviewDiscoveryContext context, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            return await this.ReadOverviewAsync(clientId, review, context, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("The provider could not return pull request metadata.");
        }
    }

    private async Task<ReviewOverviewDto> ReadOverviewAsync(Guid clientId, CodeReviewRef review, ReviewDiscoveryContext context, CancellationToken ct)
    {
        var authentication = await connectionVerifier.VerifyAsync(clientId, review.Repository.Host, context, ct).ConfigureAwait(false);
        var client = httpClientFactory.CreateClient("GitLabProvider");
        var session = new ReviewOverviewReadSession();
        var discussions = await session.ReadPagesAsync(
            response => new(GitLabPaginationHeaders.ReadGitLabHasMore(response)), async (page, pageCt) =>
            {
                using var request = GitLabConnectionVerifier.CreateAuthenticatedRequest(
                    ReviewOverviewReadSession.ScopedUri(
                        GitLabConnectionVerifier.BuildApiUri(
                            review.Repository.Host,
                            $"/projects/{Uri.EscapeDataString(review.Repository.ExternalRepositoryId)}/merge_requests/{review.Number}/discussions",
                            $"per_page=100&page={page}"), context.ProviderScopePath, review.Repository.Host.HostBaseUrl), authentication.Connection.Secret);
                return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, pageCt).ConfigureAwait(false);
            }, ct, response => GitLabReadFailures.ThrowIfDeniedOrThrottled(response)).ConfigureAwait(false);

        var messagesComplete = discussions.Complete;
        var resolutionComplete = discussions.Complete;
        var messageIds = new HashSet<string>(StringComparer.Ordinal);
        var resolved = 0;
        var unresolved = 0;
        var inspectedNotes = 0;
        foreach (var discussion in discussions.Items)
        {
            if (ReviewOverviewReadSession.Property(discussion, "notes") is not { ValueKind: JsonValueKind.Array } notes)
            {
                messagesComplete = resolutionComplete = false;
                continue;
            }

            var userNotes = new List<JsonElement>();
            inspectedNotes += notes.GetArrayLength();
            if (inspectedNotes > 3000)
            {
                messagesComplete = resolutionComplete = false;
                break;
            }

            foreach (var note in notes.EnumerateArray())
            {
                if (ReviewOverviewReadSession.Boolean(note, "system") is not { } system)
                {
                    messagesComplete = resolutionComplete = false;
                    continue;
                }

                if (system)
                {
                    continue;
                }

                if (ReviewOverviewReadSession.Identifier(note) is not { } id)
                {
                    messagesComplete = resolutionComplete = false;
                    continue;
                }

                messageIds.Add(id);
                userNotes.Add(note);
            }

            if (userNotes.Count == 0)
            {
                continue;
            }

            var individual = ReviewOverviewReadSession.Boolean(discussion, "individual_note");
            if (individual == true)
            {
                continue;
            }

            if (individual is null)
            {
                resolutionComplete = false;
                continue;
            }

            var resolvable = userNotes.Select(note => ReviewOverviewReadSession.Boolean(note, "resolvable")).ToList();
            if (resolvable.Any(value => !value.HasValue))
            {
                resolutionComplete = false;
                continue;
            }

            var resolution = userNotes.Where(note => ReviewOverviewReadSession.Boolean(note, "resolvable") == true)
                .Select(note => ReviewOverviewReadSession.Boolean(note, "resolved")).ToList();
            if (resolution.Count == 0)
            {
                continue;
            }

            if (resolution.Any(value => !value.HasValue))
            {
                resolutionComplete = false;
                continue;
            }

            if (resolution.All(value => value == true))
            {
                resolved++;
            }
            else
            {
                unresolved++;
            }
        }

        return new(
            messagesComplete ? messageIds.Count : null, resolutionComplete ? resolved : null,
            resolutionComplete ? unresolved : null, messagesComplete && resolutionComplete, true);
    }
}
