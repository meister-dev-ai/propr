// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Security;
using MeisterDev.ProPR.Infrastructure.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Reviewing;

/// <summary>
///     GitLab implementation of <see cref="IReviewThreadReplyPublisher" />. A merge request thread is a
///     discussion, and a reply is a note posted into it, which inherits the discussion's diff position and
///     resolvable state. Posting to the merge request's plain notes route instead would create a standalone
///     comment that never joins the conversation.
/// </summary>
internal sealed partial class GitLabReviewThreadReplyPublisher(
    GitLabConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory,
    ILogger<GitLabReviewThreadReplyPublisher>? logger = null) : IReviewThreadReplyPublisher
{
    // Commenting is open to Guest and above, so a refusal is far more often a token scope than a role.
    // Naming both is what lets an operator fix it without guessing which of the two is missing.
    private const string PermissionAdvice =
        "Replying in a merge request thread requires a token with the api scope and a project role that can comment, Guest or above.";

    private static readonly ActivitySource ActivitySource = new("MeisterProPR.Infrastructure");

    private readonly ILogger<GitLabReviewThreadReplyPublisher> _logger =
        logger ?? NullLogger<GitLabReviewThreadReplyPublisher>.Instance;

    public ScmProvider Provider => ScmProvider.GitLab;

    public async Task<string?> ReplyAsync(
        Guid clientId,
        ReviewThreadRef thread,
        string replyText,
        CancellationToken ct = default,
        string? quotedComment = null)
    {
        // quotedComment is ignored, for the same reason it is on Azure DevOps: the reply is a note in the
        // discussion, so the comment being answered is already directly above it. GitHub's conversation path
        // and Forgejo use it, because they post a new comment on the pull request instead.
        ArgumentNullException.ThrowIfNull(thread);

        var host = thread.Review.Repository.Host;
        var discussionId = thread.ExternalThreadId;

        using var activity = ActivitySource.StartActivity("GitLabReviewThreadReplyPublisher.Reply");
        activity?.SetTag("scm.provider", ScmProvider.GitLab.ToString());
        activity?.SetTag("provider.host", host.HostBaseUrl);
        activity?.SetTag("review.number", thread.Review.Number);
        activity?.SetTag("review.thread_id", discussionId);

        var context = await connectionVerifier.VerifyAsync(clientId, host, ct);

        using var request = GitLabConnectionVerifier.CreateAuthenticatedRequest(
            GitLabConnectionVerifier.BuildApiUri(
                host,
                $"/projects/{Uri.EscapeDataString(thread.Review.Repository.ExternalRepositoryId)}/merge_requests/{thread.Review.Number}/discussions/{Uri.EscapeDataString(discussionId)}/notes"),
            context.Connection.Secret,
            HttpMethod.Post);

        // Form-encoded rather than the query string GitLab's own example uses: the reply text is not a URL
        // parameter, and keeping it out of the request line keeps it out of every proxy and access log.
        request.Content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("body", FormatReplyText(replyText)),
        ]);

        using var response = await httpClientFactory.CreateClient("GitLabProvider").SendAsync(request, ct);
        var noteId = await ReadCreatedNoteIdAsync(response, discussionId, thread.Review.Number, ct);

        LogReplied(this._logger, discussionId, thread.Review.Number, noteId);

        return noteId;
    }

    internal static string FormatReplyText(string replyText)
    {
        return HtmlSanitizer.RenderForDisplay(replyText, ReviewBodyRenderingMode.ThreadReply).RenderedText;
    }

    private static async Task<string> ReadCreatedNoteIdAsync(
        HttpResponseMessage response,
        string discussionId,
        int mergeRequestNumber,
        CancellationToken ct)
    {
        var body = await ReadBodyAsync(response, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(Describe(DescribeFailure(response.StatusCode, discussionId, mergeRequestNumber), body));
        }

        GitLabCreatedNoteResponse? note;
        try
        {
            note = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<GitLabCreatedNoteResponse>(body);
        }
        catch (JsonException)
        {
            note = null;
        }

        // GitLab answers a created note with the note object, so its id is always present on a reply that
        // landed. An accepted status carrying no id is therefore a refusal wearing a success shape, and
        // reporting no id for it would record the thread as answered by a note that does not exist.
        if (note?.Id is not { } id)
        {
            throw new InvalidOperationException(
                Describe(
                    $"GitLab accepted the reply to discussion {discussionId} on merge request !{mergeRequestNumber} with status {(int)response.StatusCode} but returned no note.",
                    body));
        }

        return id.ToString(CultureInfo.InvariantCulture);
    }

    private static string DescribeFailure(HttpStatusCode statusCode, string discussionId, int mergeRequestNumber)
    {
        return statusCode switch
        {
            HttpStatusCode.Unauthorized =>
                $"GitLab rejected the reply to discussion {discussionId} on merge request !{mergeRequestNumber} as unauthenticated. The configured token was not accepted.",
            HttpStatusCode.Forbidden =>
                $"GitLab forbade the reply to discussion {discussionId} on merge request !{mergeRequestNumber}. {PermissionAdvice}",

            // GitLab documents 404 for a resource the caller may not access, so a hidden project and a
            // deleted thread arrive identically and the message has to offer both readings.
            HttpStatusCode.NotFound =>
                $"GitLab could not find discussion {discussionId} on merge request !{mergeRequestNumber}. The thread may no longer exist, or the token cannot see it. {PermissionAdvice}",
            _ =>
                $"GitLab failed to reply to discussion {discussionId} on merge request !{mergeRequestNumber} with status {(int)statusCode}.",
        };
    }

    private static async Task<string?> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content is null)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(body) ? null : body;
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
        Message = "GitLabReviewThreadReplyPublisher: replied to discussion {DiscussionId} on MR !{MergeRequestNumber} as note {NoteId}")]
    private static partial void LogReplied(
        ILogger logger,
        string discussionId,
        int mergeRequestNumber,
        string noteId);

    private sealed record GitLabCreatedNoteResponse([property: JsonPropertyName("id")] long? Id);
}
