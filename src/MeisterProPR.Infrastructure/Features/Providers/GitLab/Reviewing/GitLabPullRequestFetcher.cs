// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MeisterProPR.Application.Support;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Security;

namespace MeisterProPR.Infrastructure.Features.Providers.GitLab.Reviewing;

internal sealed class GitLabPullRequestFetcher(
    GitLabConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory) : IProviderPullRequestFetcher
{
    public ScmProvider Provider => ScmProvider.GitLab;

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
            throw new InvalidOperationException("GitLab pull-request fetches require a client identifier.");
        }

        var host = new ProviderHostRef(ScmProvider.GitLab, organizationUrl);
        var context = await connectionVerifier.VerifyAsync(clientId.Value, host, cancellationToken);
        var mergeRequest = await this.GetMergeRequestAsync(
            context,
            host,
            repositoryId,
            pullRequestId,
            cancellationToken);
        var changesResponse = await this.GetMergeRequestChangesAsync(
            context,
            host,
            repositoryId,
            pullRequestId,
            cancellationToken);
        var changedFiles = await this.BuildChangedFilesAsync(
            context,
            host,
            repositoryId,
            mergeRequest,
            changesResponse.Changes,
            cancellationToken);
        var allChangedFileSummaries = changesResponse.Changes
            .Select(MapSummary)
            .ToList()
            .AsReadOnly();
        var existingThreads = await this.FetchExistingThreadsAsync(
            context,
            host,
            repositoryId,
            pullRequestId,
            cancellationToken);

        return new PullRequest(
            organizationUrl,
            projectId,
            repositoryId,
            ResolveRepositoryName(mergeRequest, repositoryId),
            pullRequestId,
            iterationId,
            mergeRequest.Title ?? $"Merge Request !{pullRequestId}",
            mergeRequest.Description,
            mergeRequest.SourceBranch ?? string.Empty,
            mergeRequest.TargetBranch ?? string.Empty,
            changedFiles.AsReadOnly(),
            MapStatus(mergeRequest.State),
            existingThreads,
            compareToIterationId.HasValue ? allChangedFileSummaries : null);
    }

    private async Task<GitLabMergeRequestResponse> GetMergeRequestAsync(
        GitLabConnectionVerifier.GitLabConnectionContext context,
        ProviderHostRef host,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct)
    {
        using var request = GitLabConnectionVerifier.CreateAuthenticatedRequest(
            GitLabConnectionVerifier.BuildApiUri(
                host,
                $"/projects/{Uri.EscapeDataString(repositoryId)}/merge_requests/{pullRequestId}"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("GitLabProvider").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitLab pull-request lookup failed with status {(int)response.StatusCode}.");
        }

        return await response.Content.ReadFromJsonAsync<GitLabMergeRequestResponse>(ct)
               ?? throw new InvalidOperationException("GitLab pull-request lookup returned an empty payload.");
    }

    private async Task<GitLabMergeRequestChangesResponse> GetMergeRequestChangesAsync(
        GitLabConnectionVerifier.GitLabConnectionContext context,
        ProviderHostRef host,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct)
    {
        using var request = GitLabConnectionVerifier.CreateAuthenticatedRequest(
            GitLabConnectionVerifier.BuildApiUri(
                host,
                $"/projects/{Uri.EscapeDataString(repositoryId)}/merge_requests/{pullRequestId}/changes"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("GitLabProvider").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitLab change lookup failed with status {(int)response.StatusCode}.");
        }

        return await response.Content.ReadFromJsonAsync<GitLabMergeRequestChangesResponse>(ct)
               ?? throw new InvalidOperationException("GitLab change lookup returned an empty payload.");
    }

    private async Task<List<ChangedFile>> BuildChangedFilesAsync(
        GitLabConnectionVerifier.GitLabConnectionContext context,
        ProviderHostRef host,
        string repositoryId,
        GitLabMergeRequestResponse mergeRequest,
        IReadOnlyList<GitLabMergeRequestChangeResponse> changes,
        CancellationToken ct)
    {
        var revision = BuildRevision(mergeRequest);
        var changedFiles = new List<ChangedFile>(changes.Count);

        foreach (var change in changes)
        {
            var path = NormalizePath(change.NewPath ?? change.OldPath);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var changeType = MapChangeType(change);
            var originalPath = changeType == ChangeType.Rename ? NormalizePath(change.OldPath) : null;
            var isBinary = BinaryFileDetector.IsBinary(path);

            var headContent = string.Empty;
            var baseContent = string.Empty;
            if (!isBinary)
            {
                if (changeType != ChangeType.Delete)
                {
                    headContent =
                        await this.TryReadFileAsync(context, host, repositoryId, path, revision.HeadSha, ct) ??
                        string.Empty;
                }

                if (changeType != ChangeType.Add)
                {
                    var basePath = originalPath ?? path;
                    baseContent = await this.TryReadFileAsync(
                        context,
                        host,
                        repositoryId,
                        basePath,
                        revision.BaseSha,
                        ct) ?? string.Empty;
                }
            }

            var diff = isBinary
                ? string.Empty
                : string.IsNullOrWhiteSpace(change.Diff)
                    ? UnifiedDiffBuilder.Build(baseContent, headContent)
                    : change.Diff!;

            changedFiles.Add(new ChangedFile(path, changeType, headContent, diff, isBinary, originalPath));
        }

        return changedFiles;
    }

    private async Task<string?> TryReadFileAsync(
        GitLabConnectionVerifier.GitLabConnectionContext context,
        ProviderHostRef host,
        string repositoryId,
        string path,
        string revision,
        CancellationToken ct)
    {
        using var request = GitLabConnectionVerifier.CreateAuthenticatedRequest(
            GitLabConnectionVerifier.BuildApiUri(
                host,
                $"/projects/{Uri.EscapeDataString(repositoryId)}/repository/files/{Uri.EscapeDataString(path)}/raw",
                $"ref={Uri.EscapeDataString(revision)}"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("GitLabProvider").SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitLab file lookup failed with status {(int)response.StatusCode}.");
        }

        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<IReadOnlyList<PrCommentThread>> FetchExistingThreadsAsync(
        GitLabConnectionVerifier.GitLabConnectionContext context,
        ProviderHostRef host,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct)
    {
        using var request = GitLabConnectionVerifier.CreateAuthenticatedRequest(
            GitLabConnectionVerifier.BuildApiUri(
                host,
                $"/projects/{Uri.EscapeDataString(repositoryId)}/merge_requests/{pullRequestId}/discussions",
                "per_page=100"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("GitLabProvider").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitLab discussion lookup failed with status {(int)response.StatusCode}.");
        }

        var discussions = await response.Content.ReadFromJsonAsync<IReadOnlyList<GitLabDiscussionResponse>>(ct)
                          ?? [];

        return discussions
            .Where(discussion => !discussion.IndividualNote)
            .Select(discussion => discussion.Notes.Where(note => !note.System).ToList())
            .Where(notes => notes.Count > 0)
            .Select(notes => new PrCommentThread(
                notes[0].Id,
                NormalizePath(
                    notes.Select(note => note.Position?.NewPath ?? note.Position?.OldPath)
                        .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))),
                notes.Select(note => note.Position?.NewLine ?? note.Position?.OldLine)
                    .FirstOrDefault(line => line.HasValue),
                notes.Select(ToThreadComment).ToList().AsReadOnly(),
                notes.Any(note => note.Resolved) ? "Fixed" : "Active"))
            .ToList()
            .AsReadOnly();
    }

    private static PrThreadComment ToThreadComment(GitLabDiscussionNoteResponse note)
    {
        var externalUserId = note.Author is null
            ? null
            : note.Author.Id.ToString(CultureInfo.InvariantCulture);
        Guid? stableAuthorId = string.IsNullOrWhiteSpace(externalUserId)
            ? null
            : StableGuidGenerator.Create(externalUserId);

        return new PrThreadComment(
            note.Author?.Username ?? "Unknown",
            note.Body ?? string.Empty,
            stableAuthorId,
            note.Id,
            note.CreatedAt);
    }

    private static ChangedFileSummary MapSummary(GitLabMergeRequestChangeResponse change)
    {
        return new ChangedFileSummary(
            NormalizePath(change.NewPath ?? change.OldPath) ?? string.Empty,
            MapChangeType(change));
    }

    private static ChangeType MapChangeType(GitLabMergeRequestChangeResponse change)
    {
        if (change.NewFile)
        {
            return ChangeType.Add;
        }

        if (change.DeletedFile)
        {
            return ChangeType.Delete;
        }

        if (change.RenamedFile)
        {
            return ChangeType.Rename;
        }

        return ChangeType.Edit;
    }

    private static string ResolveRepositoryName(GitLabMergeRequestResponse mergeRequest, string repositoryId)
    {
        var candidate = NormalizePath(mergeRequest.References?.Full)
                        ?? NormalizePath(mergeRequest.References?.Short);
        return string.IsNullOrWhiteSpace(candidate)
            ? repositoryId
            : candidate.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? repositoryId;
    }

    private static ReviewRevision BuildRevision(GitLabMergeRequestResponse mergeRequest)
    {
        var headSha = NormalizeOptional(mergeRequest.DiffRefs?.HeadSha) ?? NormalizeOptional(mergeRequest.Sha);
        if (string.IsNullOrWhiteSpace(headSha))
        {
            throw new InvalidOperationException("GitLab review payload did not include a head commit SHA.");
        }

        var baseSha = NormalizeOptional(mergeRequest.DiffRefs?.BaseSha)
                      ?? NormalizeOptional(mergeRequest.DiffRefs?.StartSha)
                      ?? headSha;
        var startSha = NormalizeOptional(mergeRequest.DiffRefs?.StartSha) ?? baseSha;

        return new ReviewRevision(headSha, baseSha, startSha, headSha, $"{baseSha}...{headSha}");
    }

    private static PrStatus MapStatus(string? state)
    {
        return state?.Trim().ToLowerInvariant() switch
        {
            "opened" => PrStatus.Active,
            "merged" => PrStatus.Completed,
            _ => PrStatus.Abandoned,
        };
    }

    private static string? NormalizePath(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? null : path.Trim().TrimStart('/');
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed record GitLabMergeRequestResponse(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("description")]
        string? Description,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("source_branch")]
        string? SourceBranch,
        [property: JsonPropertyName("target_branch")]
        string? TargetBranch,
        [property: JsonPropertyName("sha")] string? Sha,
        [property: JsonPropertyName("diff_refs")]
        GitLabCodeReviewQueryService.GitLabDiffRefsResponse? DiffRefs,
        [property: JsonPropertyName("references")]
        GitLabMergeRequestReferencesResponse? References);

    private sealed record GitLabDiffRefsResponse(
        [property: JsonPropertyName("base_sha")]
        string? BaseSha,
        [property: JsonPropertyName("head_sha")]
        string? HeadSha,
        [property: JsonPropertyName("start_sha")]
        string? StartSha);

    private sealed record GitLabMergeRequestReferencesResponse(
        [property: JsonPropertyName("short")] string? Short,
        [property: JsonPropertyName("full")] string? Full);

    private sealed record GitLabMergeRequestChangesResponse(
        [property: JsonPropertyName("changes")]
        IReadOnlyList<GitLabMergeRequestChangeResponse> Changes);

    private sealed record GitLabMergeRequestChangeResponse(
        [property: JsonPropertyName("old_path")]
        string? OldPath,
        [property: JsonPropertyName("new_path")]
        string? NewPath,
        [property: JsonPropertyName("diff")] string? Diff,
        [property: JsonPropertyName("new_file")]
        bool NewFile,
        [property: JsonPropertyName("deleted_file")]
        bool DeletedFile,
        [property: JsonPropertyName("renamed_file")]
        bool RenamedFile);

    private sealed record GitLabDiscussionResponse(
        [property: JsonPropertyName("individual_note")]
        bool IndividualNote,
        [property: JsonPropertyName("notes")] IReadOnlyList<GitLabDiscussionNoteResponse> Notes);

    private sealed record GitLabDiscussionNoteResponse(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("system")] bool System,
        [property: JsonPropertyName("resolved")]
        bool Resolved,
        [property: JsonPropertyName("created_at")]
        DateTimeOffset? CreatedAt,
        [property: JsonPropertyName("author")] GitLabDiscussionAuthorResponse? Author,
        [property: JsonPropertyName("position")]
        GitLabDiscussionPositionResponse? Position);

    private sealed record GitLabDiscussionAuthorResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("username")]
        string? Username);

    private sealed record GitLabDiscussionPositionResponse(
        [property: JsonPropertyName("new_path")]
        string? NewPath,
        [property: JsonPropertyName("old_path")]
        string? OldPath,
        [property: JsonPropertyName("new_line")]
        int? NewLine,
        [property: JsonPropertyName("old_line")]
        int? OldLine);
}
