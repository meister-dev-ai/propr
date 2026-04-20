// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using MeisterProPR.Application.Support;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Security;

namespace MeisterProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;

internal sealed class ForgejoPullRequestFetcher(
    ForgejoConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory) : IProviderPullRequestFetcher
{
    public ScmProvider Provider => ScmProvider.Forgejo;

    public async Task<PullRequest> FetchAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        int? compareToIterationId = null,
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        if (!clientId.HasValue)
        {
            throw new InvalidOperationException("Forgejo pull-request fetches require a client identifier.");
        }

        var host = new ProviderHostRef(ScmProvider.Forgejo, organizationUrl);
        var context = await connectionVerifier.VerifyAsync(clientId.Value, host, cancellationToken);
        var repositoryPath = await this.ResolveRepositoryPathAsync(context, host, repositoryId, cancellationToken);
        var pullRequest = await this.GetPullRequestAsync(
            context,
            host,
            repositoryPath,
            pullRequestId,
            cancellationToken);
        var changedFilesResponse = await this.GetChangedFilesAsync(
            context,
            host,
            repositoryPath,
            pullRequestId,
            cancellationToken);
        var changedFiles = await this.BuildChangedFilesAsync(
            context,
            host,
            repositoryPath,
            pullRequest,
            changedFilesResponse,
            cancellationToken);
        var allChangedFileSummaries = changedFilesResponse
            .Select(change => new ChangedFileSummary(change.FileName, MapChangeType(change.Status)))
            .ToList()
            .AsReadOnly();
        var existingThreads = await this.FetchExistingThreadsAsync(
            context,
            host,
            repositoryPath,
            pullRequestId,
            cancellationToken);

        return new PullRequest(
            organizationUrl,
            projectId,
            repositoryId,
            repositoryPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? repositoryId,
            pullRequestId,
            iterationId,
            pullRequest.Title ?? $"Pull Request #{pullRequestId}",
            pullRequest.Body,
            pullRequest.Head?.Ref ?? string.Empty,
            pullRequest.Base?.Ref ?? string.Empty,
            changedFiles.AsReadOnly(),
            MapStatus(pullRequest),
            existingThreads,
            compareToIterationId.HasValue ? allChangedFileSummaries : null);
    }

    private async Task<string> ResolveRepositoryPathAsync(
        ForgejoConnectionVerifier.ForgejoConnectionContext context,
        ProviderHostRef host,
        string repositoryId,
        CancellationToken ct)
    {
        if (LooksLikeRepositoryPath(repositoryId))
        {
            return NormalizeRepositoryPath(repositoryId);
        }

        using var request = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
            ForgejoConnectionVerifier.BuildApiUri(host, $"/repositories/{Uri.EscapeDataString(repositoryId)}"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("ForgejoProvider").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Forgejo repository lookup failed with status {(int)response.StatusCode}.");
        }

        var repository = await response.Content.ReadFromJsonAsync<ForgejoRepositoryResponse>(ct)
                         ?? throw new InvalidOperationException("Forgejo repository lookup returned an empty payload.");
        if (string.IsNullOrWhiteSpace(repository.FullName))
        {
            throw new InvalidOperationException("Forgejo repository lookup did not return a repository full name.");
        }

        return repository.FullName.Trim();
    }

    private static bool LooksLikeRepositoryPath(string repositoryId)
    {
        return !string.IsNullOrWhiteSpace(repositoryId)
               && repositoryId.Contains('/', StringComparison.Ordinal);
    }

    private static string NormalizeRepositoryPath(string repositoryPath)
    {
        return string.Join(
            '/',
            repositoryPath
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Uri.EscapeDataString));
    }

    private async Task<ForgejoPullRequestResponse> GetPullRequestAsync(
        ForgejoConnectionVerifier.ForgejoConnectionContext context,
        ProviderHostRef host,
        string repositoryPath,
        int pullRequestId,
        CancellationToken ct)
    {
        using var request = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
            ForgejoConnectionVerifier.BuildApiUri(host, $"/repos/{repositoryPath}/pulls/{pullRequestId}"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("ForgejoProvider").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Forgejo pull-request lookup failed with status {(int)response.StatusCode}.");
        }

        return await response.Content.ReadFromJsonAsync<ForgejoPullRequestResponse>(ct)
               ?? throw new InvalidOperationException("Forgejo pull-request lookup returned an empty payload.");
    }

    private async Task<IReadOnlyList<ForgejoPullRequestFileResponse>> GetChangedFilesAsync(
        ForgejoConnectionVerifier.ForgejoConnectionContext context,
        ProviderHostRef host,
        string repositoryPath,
        int pullRequestId,
        CancellationToken ct)
    {
        using var request = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
            ForgejoConnectionVerifier.BuildApiUri(
                host,
                $"/repos/{repositoryPath}/pulls/{pullRequestId}/files",
                "limit=100"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("ForgejoProvider").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Forgejo changed-file lookup failed with status {(int)response.StatusCode}.");
        }

        return await response.Content.ReadFromJsonAsync<IReadOnlyList<ForgejoPullRequestFileResponse>>(ct)
               ?? [];
    }

    private async Task<List<ChangedFile>> BuildChangedFilesAsync(
        ForgejoConnectionVerifier.ForgejoConnectionContext context,
        ProviderHostRef host,
        string repositoryPath,
        ForgejoPullRequestResponse pullRequest,
        IReadOnlyList<ForgejoPullRequestFileResponse> files,
        CancellationToken ct)
    {
        var changedFiles = new List<ChangedFile>(files.Count);
        var headSha = pullRequest.Head?.Sha ?? string.Empty;
        var baseSha = pullRequest.Base?.Sha ?? string.Empty;

        foreach (var file in files)
        {
            var path = NormalizePath(file.FileName);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var changeType = MapChangeType(file.Status);
            var originalPath = changeType == ChangeType.Rename ? NormalizePath(file.PreviousFileName) : null;
            var isBinary = BinaryFileDetector.IsBinary(path);

            var headContent = string.Empty;
            var baseContent = string.Empty;
            if (!isBinary)
            {
                if (changeType != ChangeType.Delete)
                {
                    headContent = await this.TryReadFileAsync(context, host, repositoryPath, path, headSha, ct) ??
                                  string.Empty;
                }

                if (changeType != ChangeType.Add)
                {
                    var basePath = originalPath ?? path;
                    baseContent = await this.TryReadFileAsync(context, host, repositoryPath, basePath, baseSha, ct) ??
                                  string.Empty;
                }
            }

            var diff = isBinary
                ? string.Empty
                : string.IsNullOrWhiteSpace(file.Patch)
                    ? UnifiedDiffBuilder.Build(baseContent, headContent)
                    : file.Patch!;

            changedFiles.Add(new ChangedFile(path, changeType, headContent, diff, isBinary, originalPath));
        }

        return changedFiles;
    }

    private async Task<string?> TryReadFileAsync(
        ForgejoConnectionVerifier.ForgejoConnectionContext context,
        ProviderHostRef host,
        string repositoryPath,
        string path,
        string revision,
        CancellationToken ct)
    {
        using var request = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
            ForgejoConnectionVerifier.BuildApiUri(
                host,
                $"/repos/{repositoryPath}/contents/{Uri.EscapeDataString(path)}",
                $"ref={Uri.EscapeDataString(revision)}"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("ForgejoProvider").SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Forgejo file lookup failed with status {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<ForgejoContentResponse>(ct)
                      ?? throw new InvalidOperationException("Forgejo file lookup returned an empty payload.");
        if (string.Equals(payload.Encoding, "base64", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(payload.Content))
        {
            var raw = payload.Content.Replace("\n", string.Empty, StringComparison.Ordinal);
            return Encoding.UTF8.GetString(Convert.FromBase64String(raw));
        }

        return payload.Content;
    }

    private async Task<IReadOnlyList<PrCommentThread>> FetchExistingThreadsAsync(
        ForgejoConnectionVerifier.ForgejoConnectionContext context,
        ProviderHostRef host,
        string repositoryPath,
        int pullRequestId,
        CancellationToken ct)
    {
        var reviews = await this.GetReviewsAsync(context, host, repositoryPath, pullRequestId, ct);
        var comments = new List<ForgejoReviewCommentEnvelope>();
        foreach (var review in reviews)
        {
            var reviewComments = await this.GetReviewCommentsAsync(
                context,
                host,
                repositoryPath,
                pullRequestId,
                review.Id,
                ct);
            comments.AddRange(reviewComments.Select(comment => new ForgejoReviewCommentEnvelope(review.State, comment)));
        }

        return comments
            .GroupBy(comment => BuildThreadKey(comment.Comment))
            .Select(group => group.OrderBy(comment => comment.Comment.CreatedAt)
                .ThenBy(comment => comment.Comment.Id)
                .ToList())
            .Where(group => group.Count > 0)
            .Select(group => new PrCommentThread(
                group[0].Comment.Id,
                NormalizePath(group[0].Comment.Path),
                group[0].Comment.Position ?? group[0].Comment.OriginalPosition,
                group.Select(comment => ToThreadComment(comment.Comment)).ToList().AsReadOnly(),
                DetermineStatus(group)))
            .ToList()
            .AsReadOnly();
    }

    private async Task<IReadOnlyList<ForgejoPullReviewResponse>> GetReviewsAsync(
        ForgejoConnectionVerifier.ForgejoConnectionContext context,
        ProviderHostRef host,
        string repositoryPath,
        int pullRequestId,
        CancellationToken ct)
    {
        using var request = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
            ForgejoConnectionVerifier.BuildApiUri(
                host,
                $"/repos/{repositoryPath}/pulls/{pullRequestId}/reviews",
                "limit=100"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("ForgejoProvider").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Forgejo review lookup failed with status {(int)response.StatusCode}.");
        }

        return await response.Content.ReadFromJsonAsync<IReadOnlyList<ForgejoPullReviewResponse>>(ct)
               ?? [];
    }

    private async Task<IReadOnlyList<ForgejoPullReviewCommentResponse>> GetReviewCommentsAsync(
        ForgejoConnectionVerifier.ForgejoConnectionContext context,
        ProviderHostRef host,
        string repositoryPath,
        int pullRequestId,
        long reviewId,
        CancellationToken ct)
    {
        using var request = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
            ForgejoConnectionVerifier.BuildApiUri(
                host,
                $"/repos/{repositoryPath}/pulls/{pullRequestId}/reviews/{reviewId}/comments"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("ForgejoProvider").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Forgejo review-comment lookup failed with status {(int)response.StatusCode}.");
        }

        return await response.Content.ReadFromJsonAsync<IReadOnlyList<ForgejoPullReviewCommentResponse>>(ct)
               ?? [];
    }

    private static PrThreadComment ToThreadComment(ForgejoPullReviewCommentResponse comment)
    {
        var externalUserId = comment.User is null
            ? null
            : comment.User.Id.ToString(CultureInfo.InvariantCulture);
        Guid? stableAuthorId = string.IsNullOrWhiteSpace(externalUserId)
            ? null
            : StableGuidGenerator.Create(externalUserId);

        return new PrThreadComment(
            comment.User?.Login ?? "Unknown",
            comment.Body ?? string.Empty,
            stableAuthorId,
            comment.Id,
            comment.CreatedAt);
    }

    private static string BuildThreadKey(ForgejoPullReviewCommentResponse comment)
    {
        if (!string.IsNullOrWhiteSpace(comment.Path))
        {
            var lineNumber = comment.Position ?? comment.OriginalPosition ?? 0;
            return $"{comment.Path}:{lineNumber}";
        }

        return $"comment:{comment.Id}";
    }

    private static string DetermineStatus(IReadOnlyList<ForgejoReviewCommentEnvelope> comments)
    {
        return comments.Any(comment => string.Equals(
            comment.ReviewState,
            "APPROVED",
            StringComparison.OrdinalIgnoreCase))
            ? "Fixed"
            : "Active";
    }

    private static ChangeType MapChangeType(string? status)
    {
        return status?.Trim().ToLowerInvariant() switch
        {
            "added" => ChangeType.Add,
            "removed" => ChangeType.Delete,
            "renamed" => ChangeType.Rename,
            _ => ChangeType.Edit,
        };
    }

    private static PrStatus MapStatus(ForgejoPullRequestResponse pullRequest)
    {
        if (pullRequest.Merged || pullRequest.MergedAt is not null)
        {
            return PrStatus.Completed;
        }

        return string.Equals(pullRequest.State, "open", StringComparison.OrdinalIgnoreCase)
            ? PrStatus.Active
            : PrStatus.Abandoned;
    }

    private static string? NormalizePath(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? null : path.Trim().TrimStart('/');
    }

    private sealed record ForgejoRepositoryResponse(
        [property: JsonPropertyName("full_name")]
        string? FullName);

    private sealed record ForgejoPullRequestResponse(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("merged")] bool Merged,
        [property: JsonPropertyName("merged_at")]
        DateTimeOffset? MergedAt,
        [property: JsonPropertyName("head")] ForgejoBranchRefResponse? Head,
        [property: JsonPropertyName("base")] ForgejoBranchRefResponse? Base);

    private sealed record ForgejoBranchRefResponse(
        [property: JsonPropertyName("ref")] string? Ref,
        [property: JsonPropertyName("sha")] string? Sha);

    private sealed record ForgejoPullRequestFileResponse(
        [property: JsonPropertyName("filename")]
        string FileName,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("previous_filename")]
        string? PreviousFileName,
        [property: JsonPropertyName("patch")] string? Patch);

    private sealed record ForgejoContentResponse(
        [property: JsonPropertyName("content")]
        string? Content,
        [property: JsonPropertyName("encoding")]
        string? Encoding);

    private sealed record ForgejoPullReviewResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("state")] string? State);

    private sealed record ForgejoPullReviewCommentResponse(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("position")]
        int? Position,
        [property: JsonPropertyName("original_position")]
        int? OriginalPosition,
        [property: JsonPropertyName("created_at")]
        DateTimeOffset CreatedAt,
        [property: JsonPropertyName("user")] ForgejoUserResponse? User);

    private sealed record ForgejoUserResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("login")] string? Login);

    private sealed record ForgejoReviewCommentEnvelope(string? ReviewState, ForgejoPullReviewCommentResponse Comment);
}
