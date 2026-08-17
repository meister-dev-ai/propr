// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Security;
using MeisterDev.ProPR.Infrastructure.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;

/// <summary>
///     Forgejo implementation of <see cref="IReviewThreadReplyPublisher" />, answering with a quoted comment
///     on the pull request.
/// </summary>
/// <remarks>
///     Forgejo has no thread to reply into. A review comment carries a file path, a position and the review it
///     was submitted under, but no identifier for the conversation it belongs to. Forgejo's own interface
///     offers quote reply instead, which posts a new comment opening with a markdown blockquote of the comment
///     it answers, and blockquotes nest, so quoting an answer in a follow-up keeps the sequence readable.
///     Every answer is posted on the pull request, including answers to questions asked on a line of code, so
///     there is one code path rather than two. The blockquote identifies the comment being answered.
///     The answer is submitted as a review with a body and no inline comments, not as an issue comment. Both
///     render in the pull request timeline, but Forgejo scopes tokens by unit: an issue comment requires write
///     access to the repository's issues, a permission nothing else in ProPR requires and one a repository with
///     the issues unit disabled cannot grant. Publishing findings uses this same route, so answering requires
///     no permission beyond the repository write access reviewing already needs.
/// </remarks>
internal sealed partial class ForgejoReviewThreadReplyPublisher(
    ForgejoConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory,
    ILogger<ForgejoReviewThreadReplyPublisher>? logger = null) : IReviewThreadReplyPublisher
{
    private const string PermissionAdvice =
        "Replying on a pull request requires the same repository write access that publishing a review does.";

    private static readonly ActivitySource ActivitySource = new("MeisterProPR.Infrastructure");

    private readonly ILogger<ForgejoReviewThreadReplyPublisher> _logger =
        logger ?? NullLogger<ForgejoReviewThreadReplyPublisher>.Instance;

    /// <inheritdoc />
    public ScmProvider Provider => ScmProvider.Forgejo;

    /// <inheritdoc />
    /// <remarks>
    ///     Nothing here addresses a thread, so none is needed. It matters because Forgejo names no thread for a
    ///     comment on a line of code, and a caller that insisted on one would never bring those questions here.
    /// </remarks>
    public bool RequiresThreadIdentifier => false;

    /// <inheritdoc />
    public async Task<string?> ReplyAsync(
        Guid clientId,
        ReviewThreadRef thread,
        string replyText,
        CancellationToken ct = default,
        string? quotedComment = null)
    {
        ArgumentNullException.ThrowIfNull(thread);

        var host = thread.Review.Repository.Host;

        using var activity = ActivitySource.StartActivity("ForgejoReviewThreadReplyPublisher.Reply");
        activity?.SetTag("scm.provider", ScmProvider.Forgejo.ToString());
        activity?.SetTag("provider.host", host.HostBaseUrl);
        activity?.SetTag("review.number", thread.Review.Number);

        var context = await connectionVerifier.VerifyAsync(clientId, host, ct);

        // Resolved from the repository's own identifier rather than assembled from a scope and an id. A
        // mention configuration stores the repository the way guided selection recorded it, which is the
        // numeric id, and a pair built out of the owner and that id is shaped like a path while addressing
        // nothing.
        var repositoryPath = await this.ResolveRepositoryPathAsync(
            context,
            host,
            thread.Review.Repository.ExternalRepositoryId,
            ct);

        using var request = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
            ForgejoConnectionVerifier.BuildApiUri(
                host,
                $"/repos/{repositoryPath}/pulls/{thread.Review.Number.ToString(CultureInfo.InvariantCulture)}/reviews"),
            context.Connection.Secret,
            HttpMethod.Post);

        // A review carrying a body and no comments, submitted rather than left pending, which renders as one
        // entry in the pull request's timeline. COMMENT rather than APPROVE or REJECT, because answering a
        // question is not a verdict on the change.
        request.Content = JsonContent.Create(
            new
            {
                body = FormatReplyText(ReviewCommentQuoting.BuildQuotedReply(quotedComment, replyText)),
                @event = "COMMENT",
            });

        using var response = await httpClientFactory.CreateClient("ForgejoProvider").SendAsync(request, ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                $"Forgejo rejected the reply on pull request {thread.Review.Number} with status {(int)response.StatusCode}. {PermissionAdvice}");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Forgejo reply on pull request {thread.Review.Number} failed with status {(int)response.StatusCode}.");
        }

        LogReplied(this._logger, thread.Review.Number);

        // No identifier is reported. What this route creates is a review, and its id lives in a different
        // space from the comment ids provenance is keyed on: recording it would not merely fail to match, it
        // could match an unrelated comment that happens to share the number. The contract allows an adapter
        // that cannot name what it posted to say so, and the answer is on the pull request either way.
        return null;
    }

    private static string FormatReplyText(string replyText)
    {
        return HtmlSanitizer.RenderForDisplay(replyText, ReviewBodyRenderingMode.ThreadReply).RenderedText;
    }

    /// <summary>
    ///     Turns the stored repository identifier into the <c>owner/name</c> pair the API is addressed by,
    ///     looking it up when what was stored is the provider's own id.
    /// </summary>
    private async Task<string> ResolveRepositoryPathAsync(
        ForgejoConnectionVerifier.ForgejoConnectionContext context,
        ProviderHostRef host,
        string repositoryId,
        CancellationToken ct)
    {
        if (ProviderRepositoryPath.LooksLikeOwnerAndName(repositoryId))
        {
            return repositoryId.Trim();
        }

        using var request = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
            ForgejoConnectionVerifier.BuildApiUri(host, $"/repositories/{Uri.EscapeDataString(repositoryId)}"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("ForgejoProvider").SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Forgejo repository lookup for {repositoryId} failed with status {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<ForgejoRepositoryResponse>(ct);
        if (string.IsNullOrWhiteSpace(payload?.FullName))
        {
            throw new InvalidOperationException($"Forgejo repository lookup for {repositoryId} returned no repository name.");
        }

        return payload.FullName.Trim();
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ForgejoReviewThreadReplyPublisher: replied on PR#{PullRequestNumber}")]
    private static partial void LogReplied(ILogger logger, int pullRequestNumber);

    private sealed record ForgejoRepositoryResponse(
        [property: JsonPropertyName("full_name")]
        string? FullName);
}
