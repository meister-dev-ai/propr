// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Security;

namespace MeisterProPR.Infrastructure.Features.Providers.GitLab.Reviewing;

internal sealed class GitLabReviewThreadStatusProvider(
    GitLabConnectionVerifier connectionVerifier,
    IClientRegistry clientRegistry,
    IHttpClientFactory httpClientFactory) : IProviderReviewerThreadStatusFetcher
{
    public ScmProvider Provider => ScmProvider.GitLab;

    public async Task<IReadOnlyList<PrThreadStatusEntry>> GetReviewerThreadStatusesAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid reviewerId,
        Guid clientId,
        CancellationToken ct = default)
    {
        var host = new ProviderHostRef(ScmProvider.GitLab, organizationUrl);
        var reviewer = await clientRegistry.GetReviewerIdentityAsync(clientId, host, ct);
        if (reviewer is null)
        {
            return [];
        }

        var context = await connectionVerifier.VerifyAsync(clientId, host, ct);
        using var request = GitLabConnectionVerifier.CreateAuthenticatedRequest(
            GitLabConnectionVerifier.BuildApiUri(
                host,
                $"/projects/{Uri.EscapeDataString(repositoryId)}/merge_requests/{pullRequestId}/discussions",
                "per_page=100"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("GitLabProvider").SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitLab review thread lookup failed with status {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<IReadOnlyList<GitLabDiscussionResponse>>(ct)
                      ?? [];

        return payload
            .Where(discussion => !discussion.IndividualNote)
            .Select(discussion => new
            {
                Discussion = discussion,
                Notes = discussion.Notes.Where(note => !note.System).ToList(),
            })
            .Where(item => item.Notes.Count > 0)
            .Where(item => AuthorMatches(item.Notes[0].Author, reviewer))
            .Select(item => new PrThreadStatusEntry(
                item.Notes[0].Id,
                item.Notes.Any(note => note.Resolved) ? "Fixed" : "Active",
                ResolveFilePath(item.Notes),
                BuildCommentHistory(item.Notes),
                item.Notes.Count(note => !AuthorMatches(note.Author, reviewer))))
            .ToList()
            .AsReadOnly();
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

    private static bool AuthorMatches(GitLabAuthorResponse? author, ReviewerIdentity reviewer)
    {
        return author is not null
               && ((!string.IsNullOrWhiteSpace(author.Username) && string.Equals(
                       author.Username,
                       reviewer.Login,
                       StringComparison.OrdinalIgnoreCase))
                   || author.Id.ToString() == reviewer.ExternalUserId);
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
            builder.Append(note.Body ?? string.Empty);
        }

        return builder.ToString();
    }

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
        [property: JsonPropertyName("author")] GitLabAuthorResponse? Author,
        [property: JsonPropertyName("position")]
        GitLabPositionResponse? Position);

    private sealed record GitLabAuthorResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("username")]
        string? Username);

    private sealed record GitLabPositionResponse(
        [property: JsonPropertyName("new_path")]
        string? NewPath,
        [property: JsonPropertyName("old_path")]
        string? OldPath);
}
