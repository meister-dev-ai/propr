// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

/// <summary>
///     GitHub implementation of <see cref="IReviewThreadStatusWriter" />. Resolution is a GraphQL-only capability
///     on GitHub: the REST API does not expose review threads at all, so the resolveReviewThread and
///     unresolveReviewThread mutations are the whole surface available.
/// </summary>
internal sealed partial class GitHubReviewThreadStatusWriter(
    GitHubConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory,
    ILogger<GitHubReviewThreadStatusWriter>? logger = null) : IReviewThreadStatusWriter
{
    private const string ResolveMutation =
        "mutation ResolveReviewThread($threadId: ID!) { resolveReviewThread(input: { threadId: $threadId }) { thread { id isResolved } } }";

    private const string UnresolveMutation =
        "mutation UnresolveReviewThread($threadId: ID!) { unresolveReviewThread(input: { threadId: $threadId }) { thread { id isResolved } } }";

    // GitHub does not state this in the GraphQL reference, but both mutations are refused unless the token
    // carries repository Contents read and write; pull request access on its own is not enough. Saying so is the
    // difference between an operator knowing what to change and seeing an opaque refusal.
    private const string PermissionAdvice =
        "Resolving a review thread requires the token to hold Contents read and write on the repository, in addition to pull request access.";

    private static readonly ActivitySource ActivitySource = new("MeisterProPR.Infrastructure");

    private readonly ILogger<GitHubReviewThreadStatusWriter> _logger =
        logger ?? NullLogger<GitHubReviewThreadStatusWriter>.Instance;

    public ScmProvider Provider => ScmProvider.GitHub;

    public async Task UpdateThreadStatusAsync(
        Guid clientId,
        ReviewThreadRef thread,
        string status,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(thread);

        var resolve = ReviewThreadStatusVocabulary.ResolvesThread(status, "GitHub");
        var threadId = GitHubReviewThreadNodeId.Require(thread.ExternalThreadId, "status updates");
        var host = thread.Review.Repository.Host;

        using var activity = ActivitySource.StartActivity("GitHubReviewThreadStatusWriter.UpdateThreadStatus");
        activity?.SetTag("scm.provider", ScmProvider.GitHub.ToString());
        activity?.SetTag("provider.host", host.HostBaseUrl);
        activity?.SetTag("review.number", thread.Review.Number);
        activity?.SetTag("review.thread_id", threadId);
        activity?.SetTag("review.thread_status", status);

        var context = await connectionVerifier.VerifyAsync(clientId, host, ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, GitHubConnectionVerifier.BuildGraphQlUri(host))
        {
            Content = JsonContent.Create(
                new
                {
                    query = resolve ? ResolveMutation : UnresolveMutation,
                    variables = new
                    {
                        threadId,
                    },
                }),
        };
        await context.AuthorizeRequestAsync(request, ct);

        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);
        await EnsureMutationAppliedAsync(response, threadId, resolve, ct);

        LogThreadStatusUpdated(this._logger, threadId, thread.Review.Number, status);
    }

    private static async Task EnsureMutationAppliedAsync(
        HttpResponseMessage response,
        string threadId,
        bool resolve,
        CancellationToken ct)
    {
        var action = resolve ? "resolve" : "unresolve";
        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                Describe(
                    $"GitHub rejected the request to {action} review thread {threadId} with status {(int)response.StatusCode}. {PermissionAdvice}",
                    body));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(Describe($"GitHub failed to {action} review thread {threadId} with status {(int)response.StatusCode}.", body));
        }

        GitHubGraphQlMutationResponse? payload;
        try
        {
            payload = JsonSerializer.Deserialize<GitHubGraphQlMutationResponse>(body);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                Describe($"GitHub returned an unreadable response while attempting to {action} review thread {threadId}.", body));
        }

        // GitHub answers a refused mutation with HTTP 200 and an entry in the errors array, so a status-only
        // check would report a write that never happened as a success.
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
                    ? $"GitHub refused to {action} review thread {threadId}: {described}. {PermissionAdvice}"
                    : $"GitHub refused to {action} review thread {threadId}: {described}");
        }

        var updated = resolve
            ? payload?.Data?.ResolveReviewThread?.Thread
            : payload?.Data?.UnresolveReviewThread?.Thread;

        if (updated is null)
        {
            throw new InvalidOperationException(Describe($"GitHub returned no thread after being asked to {action} review thread {threadId}.", body));
        }

        // The mutation returns the thread it changed, so its flag is the only confirmation the write landed. A
        // thread that comes back in the state it started in means the request was accepted and did nothing.
        if (updated.IsResolved != resolve)
        {
            throw new InvalidOperationException(
                $"GitHub accepted the request to {action} review thread {threadId} but reports it as {(updated.IsResolved ? "resolved" : "unresolved")}.");
        }
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
        Message = "GitHubReviewThreadStatusWriter: set thread {ThreadId} on PR#{PullRequestNumber} to status '{Status}'")]
    private static partial void LogThreadStatusUpdated(
        ILogger logger,
        string threadId,
        int pullRequestNumber,
        string status);

    private sealed record GitHubGraphQlMutationResponse(
        [property: JsonPropertyName("data")] GitHubGraphQlMutationData? Data,
        [property: JsonPropertyName("errors")] IReadOnlyList<GitHubGraphQlError>? Errors);

    private sealed record GitHubGraphQlMutationData(
        [property: JsonPropertyName("resolveReviewThread")]
        GitHubReviewThreadMutationPayload? ResolveReviewThread,
        [property: JsonPropertyName("unresolveReviewThread")]
        GitHubReviewThreadMutationPayload? UnresolveReviewThread);

    private sealed record GitHubReviewThreadMutationPayload([property: JsonPropertyName("thread")] GitHubMutatedReviewThread? Thread);

    private sealed record GitHubMutatedReviewThread(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("isResolved")]
        bool IsResolved);

    private sealed record GitHubGraphQlError(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("message")]
        string? Message);
}
