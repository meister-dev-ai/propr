// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using MeisterDev.ProPR.Infrastructure.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

/// <summary>
///     GitHub implementation of <see cref="IReviewThreadReplyPublisher" />. The reply goes through GraphQL
///     because that is the API that takes the identifier a thread carries here: the review-thread node id.
///     The REST reply route addresses a thread by the database id of a comment already in it, which this
///     code does not hold and could only obtain by a GraphQL round trip anyway, since a thread node id
///     cannot be decoded into one and a review comment names no thread.
/// </summary>
internal sealed partial class GitHubReviewThreadReplyPublisher(
    GitHubConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory,
    ILogger<GitHubReviewThreadReplyPublisher>? logger = null) : IReviewThreadReplyPublisher
{
    private const string ReplyMutation =
        "mutation AddReviewThreadReply($threadId: ID!, $body: String!) { addPullRequestReviewThreadReply(input: { pullRequestReviewThreadId: $threadId, body: $body }) { comment { id databaseId } } }";

    // Writing into a pull request conversation is a pull request write and nothing more; unlike resolving a
    // thread it does not additionally need repository contents. Naming the narrower permission keeps an
    // operator from widening the token further than the failure calls for.
    private const string PermissionAdvice =
        "Replying in a review thread requires the token to hold Pull requests read and write on the repository, or the repo scope on a classic token.";

    private static readonly ActivitySource ActivitySource = new("MeisterProPR.Infrastructure");

    private readonly ILogger<GitHubReviewThreadReplyPublisher> _logger =
        logger ?? NullLogger<GitHubReviewThreadReplyPublisher>.Instance;

    public ScmProvider Provider => ScmProvider.GitHub;

    public async Task<string?> ReplyAsync(
        Guid clientId,
        ReviewThreadRef thread,
        string replyText,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(thread);

        var threadId = GitHubReviewThreadNodeId.Require(thread.ExternalThreadId, "replies");
        var host = thread.Review.Repository.Host;

        using var activity = ActivitySource.StartActivity("GitHubReviewThreadReplyPublisher.Reply");
        activity?.SetTag("scm.provider", ScmProvider.GitHub.ToString());
        activity?.SetTag("provider.host", host.HostBaseUrl);
        activity?.SetTag("review.number", thread.Review.Number);
        activity?.SetTag("review.thread_id", threadId);

        var context = await connectionVerifier.VerifyAsync(clientId, host, ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, GitHubConnectionVerifier.BuildGraphQlUri(host))
        {
            Content = JsonContent.Create(
                new
                {
                    query = ReplyMutation,
                    variables = new
                    {
                        threadId,
                        body = FormatReplyText(replyText),
                    },
                }),
        };
        await context.AuthorizeRequestAsync(request, ct);

        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);
        var commentId = await ReadCreatedCommentIdAsync(response, threadId, ct);

        LogReplied(this._logger, threadId, thread.Review.Number, commentId ?? "unknown");

        return commentId;
    }

    internal static string FormatReplyText(string replyText)
    {
        return HtmlSanitizer.RenderForDisplay(replyText, ReviewBodyRenderingMode.ThreadReply).RenderedText;
    }

    private static async Task<string?> ReadCreatedCommentIdAsync(
        HttpResponseMessage response,
        string threadId,
        CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                Describe(
                    $"GitHub rejected the reply to review thread {threadId} with status {(int)response.StatusCode}. {PermissionAdvice}",
                    body));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(Describe($"GitHub failed to reply to review thread {threadId} with status {(int)response.StatusCode}.", body));
        }

        GitHubReplyMutationResponse? payload;
        try
        {
            payload = JsonSerializer.Deserialize<GitHubReplyMutationResponse>(body);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(Describe($"GitHub returned an unreadable response while replying to review thread {threadId}.", body));
        }

        // GitHub answers a refused mutation with HTTP 200 and an entry in the errors array, so a status-only
        // check would report a reply that was never written as posted.
        if (payload?.Errors is { Count: > 0 } errors)
        {
            var described = string.Join(
                "; ",
                errors.Select(error => string.IsNullOrWhiteSpace(error.Type)
                    ? error.Message
                    : $"{error.Type}: {error.Message}"));
            var forbidden = errors.Any(error => string.Equals(error.Type, "FORBIDDEN", StringComparison.OrdinalIgnoreCase));

            throw new InvalidOperationException(
                forbidden
                    ? $"GitHub refused the reply to review thread {threadId}: {described}. {PermissionAdvice}"
                    : $"GitHub refused the reply to review thread {threadId}: {described}");
        }

        // The mutation returns the comment it created, so its absence means nothing was written even though
        // the call was accepted.
        var comment = payload?.Data?.AddPullRequestReviewThreadReply?.Comment;
        if (comment is null)
        {
            throw new InvalidOperationException(Describe($"GitHub accepted the reply to review thread {threadId} but returned no comment.", body));
        }

        // The database id, not the node id: provenance and thread ownership key GitHub comments on the REST
        // numeric identifier everywhere else, and a node id recorded here would never match them. Reporting
        // none is the honest degradation when GitHub omits it, since inventing an encoding is the defect
        // carrying one provider's identifier as another's was.
        if (comment.DatabaseId is not { } databaseId)
        {
            return null;
        }

        return databaseId.ToString(CultureInfo.InvariantCulture);
    }

    private static string Describe(string message, string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return message;
        }

        var singleLineBody = body.ReplaceLineEndings(" ").Trim();
        var snippet = singleLineBody.Length <= 240 ? singleLineBody : singleLineBody[..240] + "...";
        return $"{message} Response: {snippet}";
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "GitHubReviewThreadReplyPublisher: replied to thread {ThreadId} on PR#{PullRequestNumber} as comment {CommentId}")]
    private static partial void LogReplied(
        ILogger logger,
        string threadId,
        int pullRequestNumber,
        string commentId);

    private sealed record GitHubReplyMutationResponse(
        [property: JsonPropertyName("data")] GitHubReplyMutationData? Data,
        [property: JsonPropertyName("errors")] IReadOnlyList<GitHubGraphQlError>? Errors);

    private sealed record GitHubReplyMutationData(
        [property: JsonPropertyName("addPullRequestReviewThreadReply")]
        GitHubReplyMutationPayload? AddPullRequestReviewThreadReply);

    private sealed record GitHubReplyMutationPayload(
        [property: JsonPropertyName("comment")]
        GitHubCreatedReviewComment? Comment);

    private sealed record GitHubCreatedReviewComment(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("databaseId")]
        long? DatabaseId);

    private sealed record GitHubGraphQlError(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("message")]
        string? Message);
}
