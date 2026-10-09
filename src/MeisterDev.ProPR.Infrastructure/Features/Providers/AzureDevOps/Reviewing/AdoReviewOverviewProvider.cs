// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using static MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support.AdoProviderAdapterHelpers;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

internal sealed class AdoReviewOverviewProvider(
    IClientScmConnectionRepository connectionRepository,
    IClientScmScopeRepository scopeRepository,
    VssConnectionFactory connectionFactory) : IReviewOverviewProvider
{
    private const int MaxThreads = 1000;
    private const int MaxComments = 10000;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);

    public ScmProvider Provider => ScmProvider.AzureDevOps;
    internal Func<string, CancellationToken, Task<GitHttpClient>>? GitClientResolver { get; set; }

    public async Task<ReviewOverviewDto> GetOverviewAsync(Guid clientId, CodeReviewRef review, ReviewDiscoveryContext context, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        EnsureAzureDevOps(review.Repository.Host);
        var selected = await ManualReviewDiscoveryCredentials.ResolveAsync(
                connectionRepository, clientId, review.Repository.Host, context, new AdoReviewSourcePolicy().IsSelectedScopeCompatible, ct)
            .ConfigureAwait(false);
        var metadata = await connectionRepository.GetByIdAsync(clientId, context.ConnectionId, ct).ConfigureAwait(false);
        if (metadata is null || metadata.Id != context.ConnectionId || metadata.ClientId != clientId ||
            !metadata.IsActive || metadata.ProviderFamily != ScmProvider.AzureDevOps ||
            !string.Equals(metadata.VerificationStatus, "verified", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(selected.Secret))
        {
            throw new InvalidOperationException("The selected connection is unavailable for this review target.");
        }

        if (new Uri(selected.HostBaseUrl).AbsolutePath == "/")
        {
            var scopes = await scopeRepository.GetByConnectionIdAsync(clientId, selected.Id, ct).ConfigureAwait(false);
            if (!scopes.Any(scope => scope.ClientId == clientId && scope.ConnectionId == selected.Id && scope.IsEnabled &&
                                     string.Equals(scope.ScopeType, "organization", StringComparison.OrdinalIgnoreCase) &&
                                     string.Equals(scope.ScopePath.TrimEnd('/'), context.ProviderScopePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("The selected connection has no enabled organization scope for this review target.");
            }
        }

        var credentials = ToAdoCredentials(selected)
                          ?? throw new InvalidOperationException("The selected connection is unavailable for this review target.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReadTimeout);
        try
        {
            var organization = context.ProviderScopePath.TrimEnd('/');
            GitHttpClient git;
            if (this.GitClientResolver is not null)
            {
                git = await this.GitClientResolver(organization, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            else
            {
                var connection = await connectionFactory.GetOverviewConnectionAsync(organization, credentials, timeout.Token)
                    .WaitAsync(timeout.Token).ConfigureAwait(false);
                git = await connection.GetClientAsync<GitHttpClient>(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            }

            // This SDK endpoint returns all threads without a continuation token or page size.
            var threads = await git.GetThreadsAsync(
                ResolveProjectId(review.Repository), review.Repository.ExternalRepositoryId,
                review.Number, cancellationToken: timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return Count(threads, timeout.Token);
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            if (AdoReadFailures.IsThrottled(exception))
            {
                throw new ProviderThrottledException();
            }

            if (AdoReadFailures.DeniedStatus(exception) is { } status)
            {
                throw AdoReadFailures.Denial(status, AdoReadFailures.IsConnectionDenied(exception));
            }

            throw new InvalidOperationException("The provider could not return pull request metadata.");
        }
    }

    private static ReviewOverviewDto Count(List<GitPullRequestCommentThread>? threads, CancellationToken ct)
    {
        if (threads is null || threads.Count > MaxThreads)
        {
            return Unavailable();
        }

        var ids = new HashSet<int>();
        var messages = 0;
        var readComments = 0;
        var resolved = 0;
        var unresolved = 0;
        var resolutionComplete = true;
        foreach (var thread in threads)
        {
            ct.ThrowIfCancellationRequested();
            if (thread is null || thread.Id < 1 || !ids.Add(thread.Id))
            {
                return Unavailable();
            }

            if (thread.IsDeleted)
            {
                continue;
            }

            if (thread.Comments is null || thread.Comments.Count > MaxComments - readComments)
            {
                return Unavailable();
            }

            readComments += thread.Comments.Count;
            var commentIds = new HashSet<int>();
            var threadMessages = 0;
            foreach (var comment in thread.Comments)
            {
                if (comment is null || comment.Id < 1 || !commentIds.Add(comment.Id))
                {
                    return Unavailable();
                }

                if (comment.IsDeleted || comment.CommentType is CommentType.System or CommentType.CodeChange)
                {
                    continue;
                }

                if (comment.CommentType != CommentType.Text || string.IsNullOrWhiteSpace(comment.Content))
                {
                    return Unavailable();
                }

                threadMessages++;
            }

            messages += threadMessages;
            if (threadMessages == 0)
            {
                continue;
            }

            switch (thread.Status)
            {
                case CommentThreadStatus.Fixed:
                case CommentThreadStatus.Closed:
                case CommentThreadStatus.WontFix:
                case CommentThreadStatus.ByDesign:
                    resolved++;
                    break;
                case CommentThreadStatus.Active:
                case CommentThreadStatus.Pending:
                    unresolved++;
                    break;
                default:
                    resolutionComplete = false;
                    break;
            }
        }

        return new(messages, resolutionComplete ? resolved : null, resolutionComplete ? unresolved : null, resolutionComplete, true);
    }

    private static ReviewOverviewDto Unavailable() => new(null, null, null, false, true);
}
