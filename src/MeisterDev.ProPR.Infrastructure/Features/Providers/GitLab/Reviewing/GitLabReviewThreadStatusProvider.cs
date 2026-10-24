// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Security;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Support;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Reviewing;

internal sealed class GitLabReviewThreadStatusProvider(
    GitLabConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory) : IProviderReviewerThreadStatusFetcher
{
    public ScmProvider Provider => ScmProvider.GitLab;

    public async Task<IReadOnlyList<PrThreadStatusEntry>> GetReviewerThreadStatusesAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        ThreadOwnershipResolver ownership,
        Guid clientId,
        CancellationToken ct = default)
    {
        return (await this.ReadSnapshotAsync(organizationUrl, projectId, repositoryId, pullRequestId, ownership, clientId, ct)).ReviewerThreads;
    }

    public async Task<PullRequestThreadSnapshot> ReadSnapshotAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        ThreadOwnershipResolver ownership,
        Guid clientId,
        CancellationToken ct = default, Guid? connectionId = null)
    {
        ArgumentNullException.ThrowIfNull(ownership);

        var host = new ProviderHostRef(ScmProvider.GitLab, organizationUrl);
        var context = connectionId.HasValue
            ? await connectionVerifier.VerifyAsync(new ConnectionDiscoveryContext(clientId, connectionId.Value, host), ct)
            : await connectionVerifier.VerifyAsync(clientId, host, ct);

        // GitLab names an author by username, and the connection verification above is the only place the
        // authenticated one exists, so the pass's resolver is completed with it here. It is contributed into
        // the instance the caller handed down, so the consumers that run later in the same pass, and never see
        // a connection of their own, decide with the identity too.
        ownership.ContributeIdentity(new ThreadOwnerIdentity(Login: context.AuthenticatedUsername));

        var observedAt = DateTimeOffset.UtcNow;
        var payload = await ProviderRestPager.LoadAllAsync(
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
            ct, requireComplete: true);

        var statuses = payload
            .Where(discussion => !discussion.IndividualNote)
            .Select(discussion => new
            {
                Discussion = discussion,
                Notes = discussion.Notes.Where(note => !note.System).ToList(),
            })
            .Where(item => item.Notes.Count > 0)
            .Where(item => ownership.OwnsThread(ToCommentRef(item.Notes[0])))
            .Select(item => new PrThreadStatusEntry(
                item.Discussion.Id,
                item.Notes.Any(note => note.Resolved) ? "Fixed" : "Active",
                ResolveFilePath(item.Notes),
                BuildCommentHistory(item.Notes),
                item.Notes.Count(note => !ownership.OwnsComment(ToCommentRef(note)))))
            .ToList()
            .AsReadOnly();
        var raw = payload.Where(discussion => !discussion.IndividualNote).Select(discussion => new PrCommentThread(
            discussion.Id,
            ResolveFilePath(discussion.Notes),
            discussion.Notes.Select(note => note.Position?.NewLine ?? note.Position?.OldLine).FirstOrDefault(line => line.HasValue),
            discussion.Notes.Select(note => new PrThreadComment(
                note.Author?.Username ?? "Unknown", note.Body ?? "",
                ProviderSnapshotCommentIdentity.FromLogin(note.Author?.Username), note.Id, note.CreatedAt, note.System,
                note.Author?.Id.ToString(CultureInfo.InvariantCulture))).ToArray(), discussion.Notes.Any(note => note.Resolved) ? "Fixed" : "Active",
            SourceAnchor: JsonSerializer.Serialize(discussion.Notes.Select(note => note.Position)),
            ConcernAnchor: OriginalConcernAnchor(discussion))).ToArray();
        return new PullRequestThreadSnapshot(observedAt, raw, statuses) { AuthenticatedIdentity = ownership.Identity };
    }

    private static ReviewConcernAnchor OriginalConcernAnchor(GitLabDiscussionResponse discussion)
    {
        var root = discussion.Notes.FirstOrDefault(note => !note.System);
        var position = root?.OriginalPosition ?? root?.Position;
        var range = position?.LineRange;
        var side = range is not null
            ? range.Start?.Type == "new" && range.End?.Type == "new" ? "RIGHT"
            : range.Start?.Type == "old" && range.End?.Type == "old" ? "LEFT" : null
            : position?.NewLine is > 0
                ? "RIGHT"
                : position?.OldLine is > 0
                    ? "LEFT"
                    : null;
        var start = range is not null
            ? side == "RIGHT" ? range.Start?.NewLine : side == "LEFT" ? range.Start?.OldLine : null
            : side == "RIGHT"
                ? position?.NewLine
                : side == "LEFT"
                    ? position?.OldLine
                    : null;
        var end = range is not null
            ? side == "RIGHT" ? range.End?.NewLine : side == "LEFT" ? range.End?.OldLine : null
            : start;
        var anchor = new ReviewConcernAnchor
        {
            RootCommentId = root is { Id: > 0 } ? root.Id.ToString(CultureInfo.InvariantCulture) : null,
            OriginAt = root?.CreatedAt, FilePath = side == "RIGHT" ? position?.NewPath : side == "LEFT" ? position?.OldPath : null,
            Side = side, StartLine = start, EndLine = end, HeadSha = position?.HeadSha, BaseSha = position?.BaseSha,
            CommitSha = side == "RIGHT" ? position?.HeadSha : side == "LEFT" ? position?.BaseSha : null, NativeRevisionId = position?.HeadSha,
            OriginalScopeProven = position is { PositionType: "text" } && !string.IsNullOrWhiteSpace(position.HeadSha) &&
                                  !string.IsNullOrWhiteSpace(position.BaseSha),
        };
        return anchor.IsKnown ? anchor : anchor with { OriginalScopeProven = false };
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
            throw new InvalidOperationException($"GitLab review thread lookup failed with status {(int)response.StatusCode}.");
        }

        var discussions = await response.Content.ReadFromJsonAsync<IReadOnlyList<GitLabDiscussionResponse>>(ct)
                          ?? throw new InvalidOperationException("GitLab discussion lookup returned no payload.");

        return new ProviderRestPager.RestPage<GitLabDiscussionResponse>(
            discussions,
            GitLabPaginationHeaders.ReadGitLabHasMore(response));
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

    private static string? ResolveFilePath(IReadOnlyList<GitLabDiscussionNoteResponse> notes)
    {
        foreach (var note in notes)
        {
            var path = note.Position?.NewPath ?? note.Position?.OldPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }

        return null;
    }

    // A GitLab note id is unique within the merge request, so provenance resolves on it alone. The thread id
    // recorded at publish time is the discussion id, which this response carries but does not need.
    private static ThreadCommentRef ToCommentRef(GitLabDiscussionNoteResponse note)
    {
        return new ThreadCommentRef(
            null,
            note.Id.ToString(CultureInfo.InvariantCulture),
            AuthorLogin: note.Author?.Username);
    }

    private static string BuildCommentHistory(IReadOnlyList<GitLabDiscussionNoteResponse> notes)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < notes.Count; index++)
        {
            var note = notes[index];
            if (index > 0)
            {
                builder.Append('\n');
            }

            builder.Append(note.Author?.Username ?? "Unknown");
            builder.Append(": ");
            builder.Append(ReviewFindingTextFormatter.CompactBadgeHeaderForHistory(note.Body ?? string.Empty));
        }

        return builder.ToString();
    }

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
        [property: JsonPropertyName("author")] GitLabAuthorResponse? Author,
        [property: JsonPropertyName("position")]
        GitLabPositionResponse? Position,
        [property: JsonPropertyName("created_at")]
        DateTimeOffset? CreatedAt = null,
        [property: JsonPropertyName("original_position")]
        GitLabPositionResponse? OriginalPosition = null);

    private sealed record GitLabAuthorResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("username")]
        string? Username);

    private sealed record GitLabPositionResponse(
        [property: JsonPropertyName("new_path")]
        string? NewPath,
        [property: JsonPropertyName("old_path")]
        string? OldPath,
        [property: JsonPropertyName("new_line")]
        int? NewLine = null,
        [property: JsonPropertyName("old_line")]
        int? OldLine = null,
        [property: JsonPropertyName("head_sha")]
        string? HeadSha = null,
        [property: JsonPropertyName("base_sha")]
        string? BaseSha = null,
        [property: JsonPropertyName("start_sha")]
        string? StartSha = null,
        [property: JsonPropertyName("position_type")]
        string? PositionType = null,
        [property: JsonPropertyName("line_range")]
        GitLabLineRangeResponse? LineRange = null);

    private sealed record GitLabLineRangeResponse(
        [property: JsonPropertyName("start")] GitLabLineRangePointResponse? Start,
        [property: JsonPropertyName("end")] GitLabLineRangePointResponse? End);

    private sealed record GitLabLineRangePointResponse(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("new_line")]
        int? NewLine = null,
        [property: JsonPropertyName("old_line")]
        int? OldLine = null);
}
