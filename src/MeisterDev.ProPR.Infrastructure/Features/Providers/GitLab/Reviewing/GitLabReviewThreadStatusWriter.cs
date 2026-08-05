// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Net;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Reviewing;

/// <summary>
///     GitLab implementation of <see cref="IReviewThreadStatusWriter" />. A merge request thread is a discussion,
///     and its resolution is a boolean on the discussion itself, set by PUT with the target state.
/// </summary>
internal sealed partial class GitLabReviewThreadStatusWriter(
    GitLabConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory,
    ILogger<GitLabReviewThreadStatusWriter>? logger = null) : IReviewThreadStatusWriter
{
    // Resolving a thread is a write on the merge request, so a reporter-level token is refused. Naming the role
    // and the scope is what lets an operator fix it without guessing which of the two is missing.
    private const string PermissionAdvice =
        "Resolving a merge request thread requires the Developer, Maintainer or Owner role on the project, or authorship of the merge request, and a token with the api scope.";

    private static readonly ActivitySource ActivitySource = new("MeisterProPR.Infrastructure");

    private readonly ILogger<GitLabReviewThreadStatusWriter> _logger =
        logger ?? NullLogger<GitLabReviewThreadStatusWriter>.Instance;

    public ScmProvider Provider => ScmProvider.GitLab;

    public async Task UpdateThreadStatusAsync(
        Guid clientId,
        ReviewThreadRef thread,
        string status,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(thread);

        var resolved = ReviewThreadStatusVocabulary.ResolvesThread(status, "GitLab");
        var host = thread.Review.Repository.Host;
        var discussionId = thread.ExternalThreadId;

        using var activity = ActivitySource.StartActivity("GitLabReviewThreadStatusWriter.UpdateThreadStatus");
        activity?.SetTag("scm.provider", ScmProvider.GitLab.ToString());
        activity?.SetTag("provider.host", host.HostBaseUrl);
        activity?.SetTag("review.number", thread.Review.Number);
        activity?.SetTag("review.thread_id", discussionId);
        activity?.SetTag("review.thread_status", status);

        var context = await connectionVerifier.VerifyAsync(clientId, host, ct);

        // GitLab takes the target state as a query parameter on the discussion, and applies it to the whole
        // thread rather than to a single note.
        using var request = GitLabConnectionVerifier.CreateAuthenticatedRequest(
            GitLabConnectionVerifier.BuildApiUri(
                host,
                $"/projects/{Uri.EscapeDataString(thread.Review.Repository.ExternalRepositoryId)}/merge_requests/{thread.Review.Number}/discussions/{Uri.EscapeDataString(discussionId)}",
                resolved ? "resolved=true" : "resolved=false"),
            context.Connection.Secret,
            HttpMethod.Put);

        using var response = await httpClientFactory.CreateClient("GitLabProvider").SendAsync(request, ct);
        await EnsureResolutionAppliedAsync(response, discussionId, thread.Review.Number, resolved, ct);

        LogThreadStatusUpdated(this._logger, discussionId, thread.Review.Number, status);
    }

    private static async Task EnsureResolutionAppliedAsync(
        HttpResponseMessage response,
        string discussionId,
        int mergeRequestNumber,
        bool resolved,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var action = resolved ? "resolve" : "reopen";
        var body = await ReadFailureDetailAsync(response, ct);

        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized =>
                $"GitLab rejected the request to {action} discussion {discussionId} on merge request !{mergeRequestNumber} as unauthenticated. The configured token was not accepted.",
            HttpStatusCode.Forbidden =>
                $"GitLab forbade the request to {action} discussion {discussionId} on merge request !{mergeRequestNumber}. {PermissionAdvice}",
            HttpStatusCode.NotFound =>
                $"GitLab could not find discussion {discussionId} on merge request !{mergeRequestNumber}. The thread may no longer exist, or the token cannot see the project.",
            _ =>
                $"GitLab failed to {action} discussion {discussionId} on merge request !{mergeRequestNumber} with status {(int)response.StatusCode}.",
        };

        throw new InvalidOperationException(string.IsNullOrWhiteSpace(body) ? message : $"{message} Response: {body}");
    }

    private static async Task<string?> ReadFailureDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content is null)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var singleLineBody = body.ReplaceLineEndings(" ").Trim();
        return singleLineBody.Length <= 240
            ? singleLineBody
            : singleLineBody[..240] + "...";
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "GitLabReviewThreadStatusWriter: set discussion {DiscussionId} on MR !{MergeRequestNumber} to status '{Status}'")]
    private static partial void LogThreadStatusUpdated(
        ILogger logger,
        string discussionId,
        int mergeRequestNumber,
        string status);
}
