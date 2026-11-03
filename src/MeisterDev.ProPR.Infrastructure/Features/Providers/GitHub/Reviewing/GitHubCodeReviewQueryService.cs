// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

internal sealed class GitHubCodeReviewQueryService(
    GitHubConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory,
    ILogger<GitHubCodeReviewQueryService>? logger = null) : ICodeReviewQueryService
{
    private readonly ILogger<GitHubCodeReviewQueryService> _logger = logger ?? NullLogger<GitHubCodeReviewQueryService>.Instance;

    public ScmProvider Provider => ScmProvider.GitHub;

    public async Task<ReviewDiscoveryItemDto?> GetReviewAsync(
        Guid clientId,
        CodeReviewRef review,
        CancellationToken ct = default,
        ReviewDiscoveryContext? selected = null)
    {
        var context = selected is null
            ? await connectionVerifier.VerifyAsync(clientId, review.Repository.Host, ct)
            : await connectionVerifier.VerifyAsync(clientId, review.Repository.Host, selected, ct);
        var repositoryPath = await this.ResolveRepositoryPathAsync(context, review.Repository, ct);
        if (repositoryPath is null)
        {
            return null;
        }

        using var request = await context.CreateAuthenticatedRequestAsync(
            GitHubConnectionVerifier.BuildApiUri(
                review.Repository.Host,
                $"/repos/{repositoryPath}/pulls/{review.Number}"),
            ct: ct);
        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            var detail = await ReadDiagnosticBodyAsync(response, ct);
            var safeRepositoryPath = BuildRepositoryPath(review.Repository).Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            var safeDetail = (detail ?? string.Empty).Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            this._logger.LogWarning(
                "GitHub review query permission failure for repository {RepositoryPath} review {ReviewNumber} with status {StatusCode}. Detail: {Detail}",
                safeRepositoryPath,
                review.Number,
                (int)response.StatusCode,
                safeDetail);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detail)
                    ? "GitHub review query failed because the configured credential no longer has permission to access this pull request."
                    : $"GitHub review query failed because the configured credential no longer has permission to access this pull request. {detail}");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub review query failed with status {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<GitHubPullRequestResponse>(ct)
                      ?? throw new InvalidOperationException("GitHub review query returned an empty payload.");
        var latestRevision = BuildRevision(payload);
        ReviewerIdentity? requestedReviewer = null;
        if (payload.RequestedReviewers?.FirstOrDefault(reviewer => !string.IsNullOrWhiteSpace(reviewer.Login)) is { } reviewer)
        {
            var displayName = string.IsNullOrWhiteSpace(reviewer.Name) ? reviewer.Login! : reviewer.Name!;
            requestedReviewer = new ReviewerIdentity(
                review.Repository.Host,
                reviewer.Id.ToString(CultureInfo.InvariantCulture),
                reviewer.Login!,
                displayName,
                IsBot(reviewer));
        }

        return new ReviewDiscoveryItemDto(
            ScmProvider.GitHub,
            review.Repository,
            review,
            MapState(payload),
            latestRevision,
            requestedReviewer,
            payload.Title ?? $"Pull Request #{review.Number}",
            payload.HtmlUrl,
            payload.Head?.Ref,
            payload.Base?.Ref,
            Creator: payload.User is { Id: > 0 } author
                ? new PullRequestAuthor(
                    review.Repository.Host,
                    author.Id.ToString(CultureInfo.InvariantCulture),
                    author.Login,
                    author.Name,
                    IsBot(author))
                : null);
    }

    public async Task<ReviewRevision?> GetLatestRevisionAsync(
        Guid clientId,
        CodeReviewRef review,
        CancellationToken ct = default)
    {
        var item = await this.GetReviewAsync(clientId, review, ct);
        return item?.ReviewRevision;
    }

    /// <summary>Returns the owner and name to address, or null when GitHub knows no repository with the captured id.</summary>
    /// <remarks>
    ///     A discussion target stores only the numeric repository id, so a repository rebuilt from it carries that id
    ///     where the name belongs. GitHub addresses pull requests by owner and name, so the name is read by id first.
    /// </remarks>
    private async Task<string?> ResolveRepositoryPathAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context, RepositoryRef repository, CancellationToken ct)
    {
        var id = repository.ExternalRepositoryId;
        if (!id.All(char.IsAsciiDigit) || !string.Equals(
                repository.ProjectPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(), id, StringComparison.Ordinal))
        {
            return BuildRepositoryPath(repository);
        }

        using var request = await context.CreateAuthenticatedRequestAsync(GitHubConnectionVerifier.BuildApiUri(repository.Host, $"/repositories/{id}"), ct: ct);
        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub repository lookup failed with status {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<GitHubRepositoryResponse>(ct);
        var path = payload?.FullName?.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return path is { Length: 2 }
            ? $"{Uri.EscapeDataString(path[0])}/{Uri.EscapeDataString(path[1])}"
            : throw new InvalidOperationException("GitHub repository lookup did not return a repository full name.");
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

    private static ReviewRevision BuildRevision(GitHubPullRequestResponse payload)
    {
        var headSha = payload.Head?.Sha;
        var baseSha = payload.Base?.Sha;

        if (string.IsNullOrWhiteSpace(headSha) || string.IsNullOrWhiteSpace(baseSha))
        {
            throw new InvalidOperationException("GitHub review payload did not include base and head commit SHAs.");
        }

        return new ReviewRevision(
            headSha,
            baseSha,
            null,
            headSha,
            $"{baseSha}...{headSha}");
    }

    private static CodeReviewState MapState(GitHubPullRequestResponse payload)
    {
        if (payload.MergedAt is not null)
        {
            return CodeReviewState.Merged;
        }

        return payload.State?.Trim().ToLowerInvariant() switch
        {
            "open" => CodeReviewState.Open,
            "closed" => CodeReviewState.Closed,
            _ => throw new InvalidOperationException("The provider returned an unknown review state."),
        };
    }

    private static bool IsBot(GitHubReviewerResponse reviewer)
    {
        return string.Equals(reviewer.Type, "Bot", StringComparison.OrdinalIgnoreCase)
               || (reviewer.Login?.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static async Task<string?> ReadDiagnosticBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString()?.Trim();
            }
        }
        catch (JsonException)
        {
            // Malformed JSON body: fall through and surface the raw response text instead.
        }

        return body.Trim();
    }

    private sealed record GitHubPullRequestResponse(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("html_url")]
        string? HtmlUrl,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("merged_at")]
        DateTimeOffset? MergedAt,
        [property: JsonPropertyName("head")] GitHubRefResponse? Head,
        [property: JsonPropertyName("base")] GitHubRefResponse? Base,
        [property: JsonPropertyName("requested_reviewers")]
        IReadOnlyList<GitHubReviewerResponse>? RequestedReviewers,
        [property: JsonPropertyName("user")] GitHubReviewerResponse? User = null);

    private sealed record GitHubRepositoryResponse(
        [property: JsonPropertyName("full_name")]
        string? FullName);

    private sealed record GitHubRefResponse(
        [property: JsonPropertyName("ref")] string? Ref,
        [property: JsonPropertyName("sha")] string? Sha);

    private sealed record GitHubReviewerResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("login")] string? Login,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("type")] string? Type);
}
