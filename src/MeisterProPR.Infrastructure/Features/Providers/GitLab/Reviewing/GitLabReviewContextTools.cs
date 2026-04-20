// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.Features.Providers.GitLab.Reviewing;

internal sealed class GitLabReviewContextTools(
    GitLabConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory,
    IProCursorGateway proCursorGateway,
    IOptions<AiReviewOptions> options,
    ReviewContextToolsRequest request,
    ILogger<GitLabReviewContextTools> logger)
    : ProviderReviewContextToolsBase(
        proCursorGateway,
        options,
        request.CodeReview,
        request.SourceBranch,
        request.IterationId,
        request.ClientId,
        request.KnowledgeSourceIds,
        logger,
        request.ProviderScopePath)
{
    private readonly Guid? _clientId = request.ClientId;
    private readonly GitLabConnectionVerifier _connectionVerifier = connectionVerifier;
    private readonly ProviderHostRef _host = request.CodeReview.Repository.Host;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly int _pullRequestNumber = request.CodeReview.Number;
    private readonly string _repositoryId = request.CodeReview.Repository.ExternalRepositoryId;

    protected override string NormalizePath(string path)
    {
        return path.Trim().TrimStart('/');
    }

    protected override async Task<IReadOnlyList<ChangedFileSummary>> LoadChangedFilesAsync(CancellationToken ct)
    {
        var context = await this.VerifyAsync(ct);
        using var request = GitLabConnectionVerifier.CreateAuthenticatedRequest(
            GitLabConnectionVerifier.BuildApiUri(
                this._host,
                $"/projects/{Uri.EscapeDataString(this._repositoryId)}/merge_requests/{this._pullRequestNumber}/changes"),
            context.Connection.Secret);
        using var response = await this._httpClientFactory.CreateClient("GitLabProvider").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitLab change lookup failed with status {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<GitLabMergeRequestChangesResponse>(ct)
                      ?? throw new InvalidOperationException("GitLab change lookup returned an empty payload.");

        return payload.Changes
            .Select(change => new ChangedFileSummary(
                this.NormalizePath(change.NewPath ?? change.OldPath ?? string.Empty),
                change.NewFile ? ChangeType.Add :
                change.DeletedFile ? ChangeType.Delete :
                change.RenamedFile ? ChangeType.Rename : ChangeType.Edit))
            .ToList()
            .AsReadOnly();
    }

    protected override async Task<IReadOnlyList<string>> LoadFileTreeAsync(
        string normalizedBranch,
        CancellationToken ct)
    {
        var context = await this.VerifyAsync(ct);
        using var request = GitLabConnectionVerifier.CreateAuthenticatedRequest(
            GitLabConnectionVerifier.BuildApiUri(
                this._host,
                $"/projects/{Uri.EscapeDataString(this._repositoryId)}/repository/tree",
                $"recursive=true&per_page=100&ref={Uri.EscapeDataString(normalizedBranch)}"),
            context.Connection.Secret);
        using var response = await this._httpClientFactory.CreateClient("GitLabProvider").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitLab tree lookup failed with status {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<IReadOnlyList<GitLabTreeEntryResponse>>(ct)
                      ?? [];

        return payload
            .Where(entry => string.Equals(entry.Type, "blob", StringComparison.OrdinalIgnoreCase))
            .Select(entry => this.NormalizePath(entry.Path ?? string.Empty))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList()
            .AsReadOnly();
    }

    protected internal override async Task<string?> FetchRawFileContentAsync(
        string normalizedPath,
        string normalizedBranch,
        CancellationToken ct)
    {
        var context = await this.VerifyAsync(ct);
        using var request = GitLabConnectionVerifier.CreateAuthenticatedRequest(
            GitLabConnectionVerifier.BuildApiUri(
                this._host,
                $"/projects/{Uri.EscapeDataString(this._repositoryId)}/repository/files/{Uri.EscapeDataString(normalizedPath)}/raw",
                $"ref={Uri.EscapeDataString(normalizedBranch)}"),
            context.Connection.Secret);
        using var response = await this._httpClientFactory.CreateClient("GitLabProvider").SendAsync(request, ct);
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

    private async Task<GitLabConnectionVerifier.GitLabConnectionContext> VerifyAsync(CancellationToken ct)
    {
        if (!this._clientId.HasValue)
        {
            throw new InvalidOperationException("GitLab review-context tools require a client identifier.");
        }

        return await this._connectionVerifier.VerifyAsync(this._clientId.Value, this._host, ct);
    }

    private sealed record GitLabMergeRequestChangesResponse(
        [property: JsonPropertyName("changes")]
        IReadOnlyList<GitLabMergeRequestChangeResponse> Changes);

    private sealed record GitLabMergeRequestChangeResponse(
        [property: JsonPropertyName("old_path")]
        string? OldPath,
        [property: JsonPropertyName("new_path")]
        string? NewPath,
        [property: JsonPropertyName("new_file")]
        bool NewFile,
        [property: JsonPropertyName("deleted_file")]
        bool DeletedFile,
        [property: JsonPropertyName("renamed_file")]
        bool RenamedFile);

    private sealed record GitLabTreeEntryResponse(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("type")] string? Type);
}
