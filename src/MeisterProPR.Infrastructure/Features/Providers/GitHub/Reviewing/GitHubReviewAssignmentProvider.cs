// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Security;

namespace MeisterProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

internal sealed class GitHubReviewAssignmentProvider(
    GitHubConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory) : IReviewAssignmentService
{
    public ScmProvider Provider => ScmProvider.GitHub;

    public Task AddOptionalReviewerAsync(
        Guid clientId,
        CodeReviewRef review,
        ReviewerIdentity reviewer,
        CancellationToken ct = default)
    {
        return this.RequestReviewerAsync(clientId, review, reviewer, ct);
    }

    public async Task RequestReviewerAsync(
        Guid clientId,
        CodeReviewRef review,
        ReviewerIdentity reviewer,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(reviewer);

        var context = await connectionVerifier.VerifyAsync(clientId, review.Repository.Host, ct);
        var requestUri = GitHubConnectionVerifier.BuildApiUri(
            review.Repository.Host,
            $"/repos/{BuildRepositoryPath(review.Repository)}/pulls/{review.Number}/requested_reviewers");

        using var getRequest =
            GitHubConnectionVerifier.CreateAuthenticatedRequest(requestUri, context.Connection.Secret);
        using var getResponse = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(getRequest, ct);
        if (!getResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub reviewer assignment lookup failed with status {(int)getResponse.StatusCode}.");
        }

        var existing = await getResponse.Content.ReadFromJsonAsync<GitHubRequestedReviewersResponse>(ct)
                       ?? throw new InvalidOperationException("GitHub reviewer assignment lookup returned an empty payload.");
        if (existing.Users?.Any(user => string.Equals(
                user.Login,
                reviewer.Login,
                StringComparison.OrdinalIgnoreCase)) == true)
        {
            return;
        }

        using var postRequest = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = JsonContent.Create(new GitHubRequestedReviewersRequest([reviewer.Login])),
        };
        postRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.Connection.Secret);

        using var postResponse = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(postRequest, ct);
        if (postResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("GitHub reviewer assignment authentication failed.");
        }

        if (!postResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub reviewer assignment failed with status {(int)postResponse.StatusCode}.");
        }
    }

    private static string BuildRepositoryPath(RepositoryRef repository)
    {
        var repositoryName = repository.ProjectPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(repositoryName))
        {
            repositoryName = repository.ExternalRepositoryId;
        }

        return $"{Uri.EscapeDataString(repository.OwnerOrNamespace)}/{Uri.EscapeDataString(repositoryName)}";
    }

    private sealed record GitHubRequestedReviewersResponse([property: JsonPropertyName("users")] IReadOnlyList<GitHubRequestedReviewer>? Users);

    private sealed record GitHubRequestedReviewer([property: JsonPropertyName("login")] string? Login);

    private sealed record GitHubRequestedReviewersRequest(IReadOnlyList<string> Reviewers)
    {
        [JsonPropertyName("reviewers")]
        public IReadOnlyList<string> Reviewers { get; init; } = Reviewers;
    }
}
