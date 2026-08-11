// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Support;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Security;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Reviewing;

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
        CancellationToken cancellationToken = default,
        ReviewRevision? compareToReviewRevision = null,
        IReviewRepositoryWorkspace? workspace = null)
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
        var isDeltaReview = compareToIterationId.HasValue || compareToReviewRevision is not null;
        var deltaChanges = await this.TryGetDeltaChangesAsync(
                               context,
                               host,
                               repositoryId,
                               mergeRequest,
                               changesResponse.Changes,
                               compareToReviewRevision,
                               cancellationToken)
                           ?? changesResponse.Changes;
        var changedFiles = await this.BuildChangedFilesAsync(
            context,
            host,
            repositoryId,
            mergeRequest,
            deltaChanges,
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
            isDeltaReview ? allChangedFileSummaries : null,
            AuthorizedIdentityName: context.AuthenticatedUsername);
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

        var revision = BuildRevision(mergeRequest);
        var path = NormalizePath(filePath) ?? filePath;
        var isBinary = BinaryFileDetector.IsBinary(path);

        if (isBinary)
        {
            return new ChangedFile(path, ChangeType.Edit, string.Empty, string.Empty, true);
        }

        var headContent = await this.TryReadFileAsync(context, host, repositoryId, path, revision.HeadSha, cancellationToken);
        var baseContent = await this.TryReadFileAsync(context, host, repositoryId, path, revision.BaseSha, cancellationToken);

        if (headContent is null && baseContent is null)
        {
            return null;
        }

        ChangeType changeType;
        if (baseContent is null)
        {
            changeType = ChangeType.Add;
        }
        else if (headContent is null)
        {
            changeType = ChangeType.Delete;
        }
        else
        {
            changeType = ChangeType.Edit;
        }

        var diff = UnifiedDiffBuilder.Build(baseContent ?? string.Empty, headContent ?? string.Empty, path);

        return new ChangedFile(path, changeType, headContent ?? string.Empty, diff);
    }

    public async Task<IReadOnlyList<PrCommentThread>> FetchThreadsAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        // Threads-only path for the passive thread-retention observer: fetch just the discussion threads,
        // never the full merge request with changed-file content, so it stays cheap on every crawl cycle.
        if (!clientId.HasValue)
        {
            throw new InvalidOperationException("GitLab pull-request fetches require a client identifier.");
        }

        var host = new ProviderHostRef(ScmProvider.GitLab, organizationUrl);
        var context = await connectionVerifier.VerifyAsync(clientId.Value, host, cancellationToken);
        return await this.FetchExistingThreadsAsync(context, host, repositoryId, pullRequestId, cancellationToken);
    }

    public async Task<PullRequest> FetchThreadContextAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        Guid? clientId = null,
        CancellationToken cancellationToken = default,
        bool includeChangedFileManifest = false)
    {
        // Metadata and threads only: whether a reviewer thread needs answering is determined from the
        // conversation, so every content download is omitted. The changed-file names are queried only when
        // the caller requests them.
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
        var existingThreads = await this.FetchExistingThreadsAsync(
            context,
            host,
            repositoryId,
            pullRequestId,
            cancellationToken);

        IReadOnlyList<ChangedFileSummary>? changedFileManifest = null;
        if (includeChangedFileManifest)
        {
            var changes = await this.GetMergeRequestChangesAsync(
                context,
                host,
                repositoryId,
                pullRequestId,
                cancellationToken);

            changedFileManifest = (changes.Changes ?? [])
                .Select(MapSummary)
                .ToList()
                .AsReadOnly();
        }

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
            [],
            MapStatus(mergeRequest.State),
            existingThreads,
            changedFileManifest,
            AuthorizedIdentityName: context.AuthenticatedUsername);
    }

    private async Task<IReadOnlyList<GitLabMergeRequestChangeResponse>?> TryGetDeltaChangesAsync(
        GitLabConnectionVerifier.GitLabConnectionContext context,
        ProviderHostRef host,
        string repositoryId,
        GitLabMergeRequestResponse mergeRequest,
        IReadOnlyList<GitLabMergeRequestChangeResponse> allChanges,
        ReviewRevision? compareToReviewRevision,
        CancellationToken ct)
    {
        if (compareToReviewRevision is null)
        {
            return allChanges;
        }

        var currentHeadSha = NormalizeOptional(mergeRequest.DiffRefs?.HeadSha) ?? NormalizeOptional(mergeRequest.Sha);
        var baselineHeadSha = NormalizeOptional(compareToReviewRevision.HeadSha);
        if (string.IsNullOrWhiteSpace(currentHeadSha) || string.IsNullOrWhiteSpace(baselineHeadSha))
        {
            return null;
        }

        if (string.Equals(currentHeadSha, baselineHeadSha, StringComparison.Ordinal))
        {
            return [];
        }

        var compareChanges = await this.TryGetComparedChangesAsync(
            context,
            host,
            repositoryId,
            baselineHeadSha,
            currentHeadSha,
            ct);
        if (compareChanges is null)
        {
            return null;
        }

        if (compareChanges.Count == 0)
        {
            return [];
        }

        var deltaPaths = BuildDeltaPathSet(compareChanges);
        var filteredChanges = allChanges
            .Where(change => IsDeltaChange(change, deltaPaths))
            .ToList();

        return filteredChanges.Count == compareChanges.Count
            ? filteredChanges.AsReadOnly()
            : null;
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

        var changes = await response.Content.ReadFromJsonAsync<GitLabMergeRequestChangesResponse>(ct)
                      ?? throw new InvalidOperationException("GitLab change lookup returned an empty payload.");

        // This listing is not paginated: past the host's diff limits GitLab drops the remainder from the
        // payload and says so in an overflow flag, which is the only signal that the change set is short.
        if (changes.Overflow)
        {
            throw new InvalidOperationException(ProviderPaginationFailure.ProviderTruncated($"GitLab's change listing for merge request {pullRequestId}"));
        }

        return changes;
    }

    private async Task<IReadOnlyList<GitLabMergeRequestChangeResponse>?> TryGetComparedChangesAsync(
        GitLabConnectionVerifier.GitLabConnectionContext context,
        ProviderHostRef host,
        string repositoryId,
        string fromRevision,
        string toRevision,
        CancellationToken ct)
    {
        using var request = GitLabConnectionVerifier.CreateAuthenticatedRequest(
            GitLabConnectionVerifier.BuildApiUri(
                host,
                $"/projects/{Uri.EscapeDataString(repositoryId)}/repository/compare",
                $"from={Uri.EscapeDataString(fromRevision)}&to={Uri.EscapeDataString(toRevision)}"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("GitLabProvider").SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<GitLabCompareResponse>(ct);
        return payload?.Diffs ?? [];
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
            var changedFile = await this.BuildChangedFileAsync(context, host, repositoryId, change, revision, ct);
            if (changedFile is not null)
            {
                changedFiles.Add(changedFile);
            }
        }

        return changedFiles;
    }

    private async Task<ChangedFile?> BuildChangedFileAsync(
        GitLabConnectionVerifier.GitLabConnectionContext context,
        ProviderHostRef host,
        string repositoryId,
        GitLabMergeRequestChangeResponse change,
        ReviewRevision revision,
        CancellationToken ct)
    {
        var path = NormalizePath(change.NewPath ?? change.OldPath);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
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

        string diff;
        if (isBinary)
        {
            diff = string.Empty;
        }
        else if (string.IsNullOrWhiteSpace(change.Diff))
        {
            diff = UnifiedDiffBuilder.Build(baseContent, headContent, path);
        }
        else
        {
            diff = change.Diff!;
        }

        return new ChangedFile(path, changeType, headContent, diff, isBinary, originalPath);
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
        var discussions = await ProviderRestPager.LoadAllAsync(
            (page, pageSize, pageCt) => this.GetDiscussionPageAsync(
                context,
                host,
                repositoryId,
                pullRequestId,
                page,
                pageSize,
                pageCt),
            IdentifyDiscussion,
            $"GitLab's discussion listing for merge request {pullRequestId}",
            ct);

        return discussions
            .Where(discussion => !discussion.IndividualNote)
            .Select(discussion => new
            {
                discussion.Id,
                Notes = discussion.Notes.Where(note => !note.System).ToList(),
            })
            .Where(item => item.Notes.Count > 0)
            .Select(item => new PrCommentThread(
                item.Id,
                NormalizePath(
                    item.Notes.Select(note => note.Position?.NewPath ?? note.Position?.OldPath)
                        .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))),
                item.Notes.Select(note => note.Position?.NewLine ?? note.Position?.OldLine)
                    .FirstOrDefault(line => line.HasValue),
                item.Notes.Select(ToThreadComment).ToList().AsReadOnly(),
                item.Notes.Any(note => note.Resolved) ? "Fixed" : "Active"))
            .ToList()
            .AsReadOnly();
    }

    private async Task<ProviderRestPager.RestPage<GitLabDiscussionResponse>> GetDiscussionPageAsync(
        GitLabConnectionVerifier.GitLabConnectionContext context,
        ProviderHostRef host,
        string repositoryId,
        int pullRequestId,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        using var request = GitLabConnectionVerifier.CreateAuthenticatedRequest(
            GitLabConnectionVerifier.BuildApiUri(
                host,
                $"/projects/{Uri.EscapeDataString(repositoryId)}/merge_requests/{pullRequestId}/discussions",
                BuildPageQuery(page, pageSize)),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("GitLabProvider").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitLab discussion lookup failed with status {(int)response.StatusCode}.");
        }

        var discussions = await response.Content.ReadFromJsonAsync<IReadOnlyList<GitLabDiscussionResponse>>(ct)
                          ?? [];

        return new ProviderRestPager.RestPage<GitLabDiscussionResponse>(
            discussions,
            ProviderPaginationHeaders.ReadGitLabHasMore(response));
    }

    // The first page asks for a size and no page number, which is the request a single-page collection made
    // before it was read across pages: GitLab serves page one either way.
    private static string BuildPageQuery(int page, int pageSize)
    {
        var size = $"per_page={pageSize.ToString(CultureInfo.InvariantCulture)}";

        return page <= 1 ? size : $"{size}&page={page.ToString(CultureInfo.InvariantCulture)}";
    }

    // A discussion is named by its own id. The notes it holds are the fallback, so that discussions arriving
    // without one stay distinct from each other rather than collapsing into a single entry.
    private static string IdentifyDiscussion(GitLabDiscussionResponse discussion)
    {
        return string.IsNullOrWhiteSpace(discussion.Id)
            ? string.Join(',', discussion.Notes.Select(note => note.Id.ToString(CultureInfo.InvariantCulture)))
            : discussion.Id;
    }

    private static PrThreadComment ToThreadComment(GitLabDiscussionNoteResponse note)
    {
        var externalUserId = note.Author?.Username;
        Guid? stableAuthorId = string.IsNullOrWhiteSpace(externalUserId)
            ? null
            : StableGuidGenerator.Create(externalUserId);

        return new PrThreadComment(
            note.Author?.Username ?? "Unknown",
            note.Body ?? string.Empty,
            stableAuthorId,
            note.Id,
            note.CreatedAt,
            // Discussions already drop GitLab's own activity notes, and carrying the flag as well keeps the
            // provider boundary uniform for anything that reads a thread without going through that filter.
            note.System);
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

    private static HashSet<string> BuildDeltaPathSet(IEnumerable<GitLabMergeRequestChangeResponse> changes)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            if (NormalizePath(change.NewPath ?? change.OldPath) is { } path)
            {
                paths.Add(path);
            }

            if (NormalizePath(change.OldPath) is { } oldPath)
            {
                paths.Add(oldPath);
            }
        }

        return paths;
    }

    private static bool IsDeltaChange(GitLabMergeRequestChangeResponse change, IReadOnlySet<string> deltaPaths)
    {
        return (NormalizePath(change.NewPath ?? change.OldPath) is { } path && deltaPaths.Contains(path))
               || (NormalizePath(change.OldPath) is { } oldPath && deltaPaths.Contains(oldPath));
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

    private sealed record GitLabMergeRequestReferencesResponse(
        [property: JsonPropertyName("short")] string? Short,
        [property: JsonPropertyName("full")] string? Full);

    private sealed record GitLabMergeRequestChangesResponse(
        [property: JsonPropertyName("changes")]
        IReadOnlyList<GitLabMergeRequestChangeResponse> Changes,
        [property: JsonPropertyName("overflow")]
        bool Overflow = false);

    private sealed record GitLabCompareResponse([property: JsonPropertyName("diffs")] IReadOnlyList<GitLabMergeRequestChangeResponse>? Diffs);

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
        [property: JsonPropertyName("id")] string? Id,
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
