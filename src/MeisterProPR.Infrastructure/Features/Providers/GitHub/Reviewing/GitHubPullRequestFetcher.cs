// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Support;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Security;

namespace MeisterProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

internal sealed class GitHubPullRequestFetcher(
    GitHubConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory) : IProviderPullRequestFetcher
{
    private const string ReviewThreadsQuery =
        "query ReviewThreads($owner: String!, $name: String!, $pullRequestNumber: Int!) { repository(owner: $owner, name: $name) { pullRequest(number: $pullRequestNumber) { reviewThreads(first: 100) { nodes { isResolved path line comments(first: 100) { nodes { databaseId body createdAt author { login ... on User { databaseId } ... on Bot { databaseId } } } } } } } } }";

    public ScmProvider Provider => ScmProvider.GitHub;

    public async Task<PullRequest> FetchAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        int? compareToIterationId = null,
        Guid? clientId = null,
        CancellationToken cancellationToken = default,
        ReviewRevision? compareToReviewRevision = null,
        IReviewRepositoryWorkspace? workspace = null)
    {
        if (!clientId.HasValue)
        {
            throw new InvalidOperationException("GitHub pull-request fetches require a client identifier.");
        }

        var host = new ProviderHostRef(ScmProvider.GitHub, organizationUrl);
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
        var isDeltaReview = compareToIterationId.HasValue || compareToReviewRevision is not null;
        var deltaChangedFilesResponse = await this.TryGetDeltaFilesAsync(
                                            context,
                                            host,
                                            repositoryPath,
                                            pullRequest,
                                            changedFilesResponse,
                                            compareToReviewRevision,
                                            cancellationToken)
                                        ?? changedFilesResponse;
        var changedFiles = await this.BuildChangedFilesAsync(
            context,
            host,
            repositoryPath,
            pullRequest,
            deltaChangedFilesResponse,
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
            isDeltaReview ? allChangedFileSummaries : null,
            AuthorizedIdentityName: context.AuthenticatedActorLogin);
    }

    public async Task<ChangedFile?> FetchFileDiffAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        string filePath,
        int? compareToIterationId = null,
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        if (!clientId.HasValue)
        {
            throw new InvalidOperationException("GitHub pull-request fetches require a client identifier.");
        }

        var host = new ProviderHostRef(ScmProvider.GitHub, organizationUrl);
        var context = await connectionVerifier.VerifyAsync(clientId.Value, host, cancellationToken);
        var repositoryPath = await this.ResolveRepositoryPathAsync(context, host, repositoryId, cancellationToken);
        var pullRequest = await this.GetPullRequestAsync(
            context,
            host,
            repositoryPath,
            pullRequestId,
            cancellationToken);

        var headSha = pullRequest.Head?.Sha ?? string.Empty;
        var baseSha = pullRequest.Base?.Sha ?? string.Empty;
        var path = NormalizePath(filePath) ?? filePath;
        var isBinary = BinaryFileDetector.IsBinary(path);

        if (isBinary)
        {
            return new ChangedFile(path, ChangeType.Edit, string.Empty, string.Empty, true);
        }

        var headContent = await this.TryReadFileAsync(context, host, repositoryPath, path, headSha, cancellationToken);
        var baseContent = await this.TryReadFileAsync(context, host, repositoryPath, path, baseSha, cancellationToken);

        if (headContent is null && baseContent is null)
        {
            return null;
        }

        var changeType = baseContent is null
            ? ChangeType.Add
            : headContent is null
                ? ChangeType.Delete
                : ChangeType.Edit;

        var diff = UnifiedDiffBuilder.Build(baseContent ?? string.Empty, headContent ?? string.Empty);

        return new ChangedFile(path, changeType, headContent ?? string.Empty, diff, false);
    }

    private async Task<IReadOnlyList<GitHubPullRequestFileResponse>?> TryGetDeltaFilesAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        ProviderHostRef host,
        string repositoryPath,
        GitHubPullRequestResponse pullRequest,
        IReadOnlyList<GitHubPullRequestFileResponse> allFiles,
        ReviewRevision? compareToReviewRevision,
        CancellationToken ct)
    {
        if (compareToReviewRevision is null)
        {
            return allFiles;
        }

        var currentHeadSha = NormalizeRevision(pullRequest.Head?.Sha);
        var baselineHeadSha = NormalizeRevision(compareToReviewRevision.HeadSha);
        if (string.IsNullOrWhiteSpace(currentHeadSha) || string.IsNullOrWhiteSpace(baselineHeadSha))
        {
            return null;
        }

        if (string.Equals(currentHeadSha, baselineHeadSha, StringComparison.Ordinal))
        {
            return [];
        }

        var compareFiles = await this.TryGetComparedFilesAsync(
            context,
            host,
            repositoryPath,
            baselineHeadSha,
            currentHeadSha,
            ct);
        if (compareFiles is null)
        {
            return null;
        }

        if (compareFiles.Count == 0)
        {
            return [];
        }

        var deltaPaths = BuildDeltaPathSet(compareFiles);
        var filteredFiles = allFiles
            .Where(file => IsDeltaFile(file, deltaPaths))
            .ToList();

        return filteredFiles.Count == compareFiles.Count
            ? filteredFiles.AsReadOnly()
            : null;
    }

    private async Task<string> ResolveRepositoryPathAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        ProviderHostRef host,
        string repositoryId,
        CancellationToken ct)
    {
        if (LooksLikeRepositoryPath(repositoryId))
        {
            return NormalizeRepositoryPath(repositoryId);
        }

        using var request = await context.CreateAuthenticatedRequestAsync(
            GitHubConnectionVerifier.BuildApiUri(host, $"/repositories/{Uri.EscapeDataString(repositoryId)}"),
            ct: ct);
        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub repository lookup failed with status {(int)response.StatusCode}.");
        }

        var repository = await response.Content.ReadFromJsonAsync<GitHubRepositoryResponse>(ct)
                         ?? throw new InvalidOperationException("GitHub repository lookup returned an empty payload.");
        if (string.IsNullOrWhiteSpace(repository.FullName))
        {
            throw new InvalidOperationException("GitHub repository lookup did not return a repository full name.");
        }

        return repository.FullName.Trim();
    }

    private static bool LooksLikeRepositoryPath(string repositoryId)
    {
        return !string.IsNullOrWhiteSpace(repositoryId)
               && repositoryId.Contains('/', StringComparison.Ordinal)
               && repositoryId.Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 2;
    }

    private static string NormalizeRepositoryPath(string repositoryId)
    {
        return string.Join(
            '/',
            repositoryId.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private async Task<GitHubPullRequestResponse> GetPullRequestAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        ProviderHostRef host,
        string repositoryPath,
        int pullRequestId,
        CancellationToken ct)
    {
        using var request = await context.CreateAuthenticatedRequestAsync(
            GitHubConnectionVerifier.BuildApiUri(host, $"/repos/{repositoryPath}/pulls/{pullRequestId}"),
            ct: ct);
        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub pull-request lookup failed with status {(int)response.StatusCode}.");
        }

        return await response.Content.ReadFromJsonAsync<GitHubPullRequestResponse>(ct)
               ?? throw new InvalidOperationException("GitHub pull-request lookup returned an empty payload.");
    }

    private async Task<IReadOnlyList<GitHubPullRequestFileResponse>> GetChangedFilesAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        ProviderHostRef host,
        string repositoryPath,
        int pullRequestId,
        CancellationToken ct)
    {
        using var request = await context.CreateAuthenticatedRequestAsync(
            GitHubConnectionVerifier.BuildApiUri(
                host,
                $"/repos/{repositoryPath}/pulls/{pullRequestId}/files",
                "per_page=100"),
            ct: ct);
        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub changed-file lookup failed with status {(int)response.StatusCode}.");
        }

        return await response.Content.ReadFromJsonAsync<IReadOnlyList<GitHubPullRequestFileResponse>>(ct)
               ?? [];
    }

    private async Task<IReadOnlyList<GitHubPullRequestFileResponse>?> TryGetComparedFilesAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        ProviderHostRef host,
        string repositoryPath,
        string baseRevision,
        string headRevision,
        CancellationToken ct)
    {
        using var request = await context.CreateAuthenticatedRequestAsync(
            GitHubConnectionVerifier.BuildApiUri(
                host,
                $"/repos/{repositoryPath}/compare/{Uri.EscapeDataString(baseRevision)}...{Uri.EscapeDataString(headRevision)}",
                "per_page=100"),
            ct: ct);
        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<GitHubCompareResponse>(ct);
        return payload?.Files ?? [];
    }

    private async Task<List<ChangedFile>> BuildChangedFilesAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        ProviderHostRef host,
        string repositoryPath,
        GitHubPullRequestResponse pullRequest,
        IReadOnlyList<GitHubPullRequestFileResponse> files,
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
        GitHubConnectionVerifier.GitHubConnectionContext context,
        ProviderHostRef host,
        string repositoryPath,
        string path,
        string revision,
        CancellationToken ct)
    {
        using var request = await context.CreateAuthenticatedRequestAsync(
            GitHubConnectionVerifier.BuildApiUri(
                host,
                $"/repos/{repositoryPath}/contents/{Uri.EscapeDataString(path)}",
                $"ref={Uri.EscapeDataString(revision)}"),
            ct: ct);
        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub file lookup failed with status {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<GitHubContentResponse>(ct)
                      ?? throw new InvalidOperationException("GitHub file lookup returned an empty payload.");
        if (string.Equals(payload.Encoding, "base64", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(payload.Content))
        {
            var raw = payload.Content.Replace("\n", string.Empty, StringComparison.Ordinal);
            return Encoding.UTF8.GetString(Convert.FromBase64String(raw));
        }

        return payload.Content;
    }

    private async Task<IReadOnlyList<PrCommentThread>> FetchExistingThreadsAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        ProviderHostRef host,
        string repositoryPath,
        int pullRequestId,
        CancellationToken ct)
    {
        var parts = repositoryPath.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            throw new InvalidOperationException("GitHub repository path must be in owner/name format.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, GitHubConnectionVerifier.BuildGraphQlUri(host))
        {
            Content = JsonContent.Create(
                new
                {
                    query = ReviewThreadsQuery,
                    variables = new
                    {
                        owner = parts[0],
                        name = parts[1],
                        pullRequestNumber = pullRequestId,
                    },
                }),
        };
        await context.AuthorizeRequestAsync(request, ct);

        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub thread lookup failed with status {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<GitHubGraphQlResponse>(ct)
                      ?? throw new InvalidOperationException("GitHub thread lookup returned an empty payload.");
        var threads = payload.Data?.Repository?.PullRequest?.ReviewThreads?.Nodes ?? [];

        return threads
            .Where(thread => thread.Comments.Nodes.Count > 0)
            .Select(thread => new PrCommentThread(
                thread.Comments.Nodes[0].DatabaseId ?? 0,
                NormalizePath(thread.Path),
                thread.Line,
                thread.Comments.Nodes.Select(ToThreadComment).ToList().AsReadOnly(),
                thread.IsResolved ? "Fixed" : "Active"))
            .ToList()
            .AsReadOnly();
    }

    private static PrThreadComment ToThreadComment(GitHubReviewCommentNode comment)
    {
        var externalUserId = comment.Author?.Login ?? comment.Author?.DatabaseId?.ToString();
        Guid? stableAuthorId = string.IsNullOrWhiteSpace(externalUserId)
            ? null
            : StableGuidGenerator.Create(externalUserId);

        return new PrThreadComment(
            comment.Author?.Login ?? "Unknown",
            comment.Body ?? string.Empty,
            stableAuthorId,
            comment.DatabaseId ?? 0,
            comment.CreatedAt);
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

    private static PrStatus MapStatus(GitHubPullRequestResponse pullRequest)
    {
        if (pullRequest.MergedAt is not null)
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

    private static string? NormalizeRevision(string? revision)
    {
        return string.IsNullOrWhiteSpace(revision) ? null : revision.Trim();
    }

    private static HashSet<string> BuildDeltaPathSet(IEnumerable<GitHubPullRequestFileResponse> files)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (NormalizePath(file.FileName) is { } filePath)
            {
                paths.Add(filePath);
            }

            if (NormalizePath(file.PreviousFileName) is { } previousPath)
            {
                paths.Add(previousPath);
            }
        }

        return paths;
    }

    private static bool IsDeltaFile(GitHubPullRequestFileResponse file, IReadOnlySet<string> deltaPaths)
    {
        return (NormalizePath(file.FileName) is { } filePath && deltaPaths.Contains(filePath))
               || (NormalizePath(file.PreviousFileName) is { } previousPath && deltaPaths.Contains(previousPath));
    }

    private sealed record GitHubRepositoryResponse(
        [property: JsonPropertyName("full_name")]
        string? FullName);

    private sealed record GitHubPullRequestResponse(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("merged_at")]
        DateTimeOffset? MergedAt,
        [property: JsonPropertyName("head")] GitHubRefResponse? Head,
        [property: JsonPropertyName("base")] GitHubRefResponse? Base);

    private sealed record GitHubRefResponse(
        [property: JsonPropertyName("ref")] string? Ref,
        [property: JsonPropertyName("sha")] string? Sha);

    private sealed record GitHubPullRequestFileResponse(
        [property: JsonPropertyName("filename")]
        string FileName,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("previous_filename")]
        string? PreviousFileName,
        [property: JsonPropertyName("patch")] string? Patch);

    private sealed record GitHubCompareResponse([property: JsonPropertyName("files")] IReadOnlyList<GitHubPullRequestFileResponse>? Files);

    private sealed record GitHubContentResponse(
        [property: JsonPropertyName("content")]
        string? Content,
        [property: JsonPropertyName("encoding")]
        string? Encoding);

    private sealed record GitHubGraphQlResponse([property: JsonPropertyName("data")] GitHubGraphQlData? Data);

    private sealed record GitHubGraphQlData(
        [property: JsonPropertyName("repository")]
        GitHubGraphQlRepository? Repository);

    private sealed record GitHubGraphQlRepository(
        [property: JsonPropertyName("pullRequest")]
        GitHubGraphQlPullRequest? PullRequest);

    private sealed record GitHubGraphQlPullRequest(
        [property: JsonPropertyName("reviewThreads")]
        GitHubReviewThreadsConnection? ReviewThreads);

    private sealed record GitHubReviewThreadsConnection([property: JsonPropertyName("nodes")] IReadOnlyList<GitHubReviewThreadNode>? Nodes);

    private sealed record GitHubReviewThreadNode(
        [property: JsonPropertyName("isResolved")]
        bool IsResolved,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("line")] int? Line,
        [property: JsonPropertyName("comments")]
        GitHubReviewCommentsConnection Comments);

    private sealed record GitHubReviewCommentsConnection([property: JsonPropertyName("nodes")] IReadOnlyList<GitHubReviewCommentNode> Nodes);

    private sealed record GitHubReviewCommentNode(
        [property: JsonPropertyName("databaseId")]
        long? DatabaseId,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("createdAt")]
        DateTimeOffset CreatedAt,
        [property: JsonPropertyName("author")] GitHubActorNode? Author);

    private sealed record GitHubActorNode(
        [property: JsonPropertyName("login")] string? Login,
        [property: JsonPropertyName("databaseId")]
        long? DatabaseId);
}
