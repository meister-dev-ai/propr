// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;

internal sealed class ForgejoReviewContextTools(
    ForgejoConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory,
    IProCursorGateway proCursorGateway,
    IOptions<AiReviewOptions> options,
    ReviewContextToolsRequest request,
    ILogger<ForgejoReviewContextTools> logger)
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
    private readonly ForgejoConnectionVerifier _connectionVerifier = connectionVerifier;
    private readonly ProviderHostRef _host = request.CodeReview.Repository.Host;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly int _pullRequestNumber = request.CodeReview.Number;
    private readonly string _repositoryPath = BuildRepositoryPath(request.CodeReview.Repository);

    protected override string NormalizePath(string path)
    {
        return path.Trim().TrimStart('/');
    }

    protected override async Task<IReadOnlyList<ChangedFileSummary>> LoadChangedFilesAsync(CancellationToken ct)
    {
        var context = await this.VerifyAsync(ct);
        using var request = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
            ForgejoConnectionVerifier.BuildApiUri(
                this._host,
                $"/repos/{this._repositoryPath}/pulls/{this._pullRequestNumber}/files",
                "limit=100"),
            context.Connection.Secret);
        using var response = await this._httpClientFactory.CreateClient("ForgejoProvider").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Forgejo changed-file lookup failed with status {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<IReadOnlyList<ForgejoPullRequestFileResponse>>(ct)
                      ?? [];

        return payload
            .Select(file => new ChangedFileSummary(this.NormalizePath(file.FileName), MapChangeType(file.Status)))
            .ToList()
            .AsReadOnly();
    }

    protected override async Task<IReadOnlyList<string>> LoadFileTreeAsync(
        string normalizedBranch,
        CancellationToken ct)
    {
        var context = await this.VerifyAsync(ct);
        using var branchRequest = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
            ForgejoConnectionVerifier.BuildApiUri(
                this._host,
                $"/repos/{this._repositoryPath}/branches/{Uri.EscapeDataString(normalizedBranch)}"),
            context.Connection.Secret);
        using var branchResponse =
            await this._httpClientFactory.CreateClient("ForgejoProvider").SendAsync(branchRequest, ct);
        if (!branchResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Forgejo branch lookup failed with status {(int)branchResponse.StatusCode}.");
        }

        var branchPayload = await branchResponse.Content.ReadFromJsonAsync<ForgejoBranchResponse>(ct)
                            ?? throw new InvalidOperationException("Forgejo branch lookup returned an empty payload.");
        var commitSha = branchPayload.Commit?.Id ?? branchPayload.Commit?.Sha;
        if (string.IsNullOrWhiteSpace(commitSha))
        {
            return [];
        }

        using var treeRequest = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
            ForgejoConnectionVerifier.BuildApiUri(
                this._host,
                $"/repos/{this._repositoryPath}/git/trees/{commitSha}",
                "recursive=true"),
            context.Connection.Secret);
        using var treeResponse =
            await this._httpClientFactory.CreateClient("ForgejoProvider").SendAsync(treeRequest, ct);
        if (!treeResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Forgejo tree lookup failed with status {(int)treeResponse.StatusCode}.");
        }

        var treePayload = await treeResponse.Content.ReadFromJsonAsync<ForgejoTreeResponse>(ct)
                          ?? throw new InvalidOperationException("Forgejo tree lookup returned an empty payload.");

        return treePayload.Tree
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
        using var request = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
            ForgejoConnectionVerifier.BuildApiUri(
                this._host,
                $"/repos/{this._repositoryPath}/contents/{Uri.EscapeDataString(normalizedPath)}",
                $"ref={Uri.EscapeDataString(normalizedBranch)}"),
            context.Connection.Secret);
        using var response = await this._httpClientFactory.CreateClient("ForgejoProvider").SendAsync(request, ct);
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

    private async Task<ForgejoConnectionVerifier.ForgejoConnectionContext> VerifyAsync(CancellationToken ct)
    {
        if (!this._clientId.HasValue)
        {
            throw new InvalidOperationException("Forgejo review-context tools require a client identifier.");
        }

        return await this._connectionVerifier.VerifyAsync(this._clientId.Value, this._host, ct);
    }

    private static string BuildRepositoryPath(RepositoryRef repository)
    {
        if (!string.IsNullOrWhiteSpace(repository.ProjectPath) && repository.ProjectPath.Contains('/'))
        {
            return repository.ProjectPath.Trim();
        }

        var repositoryName = repository.ProjectPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(repositoryName))
        {
            repositoryName = repository.ExternalRepositoryId;
        }

        return $"{Uri.EscapeDataString(repository.OwnerOrNamespace)}/{Uri.EscapeDataString(repositoryName)}";
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

    private sealed record ForgejoPullRequestFileResponse(
        [property: JsonPropertyName("filename")]
        string FileName,
        [property: JsonPropertyName("status")] string? Status);

    private sealed record ForgejoBranchResponse([property: JsonPropertyName("commit")] ForgejoBranchCommitResponse? Commit);

    private sealed record ForgejoBranchCommitResponse(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("sha")] string? Sha);

    private sealed record ForgejoTreeResponse([property: JsonPropertyName("tree")] IReadOnlyList<ForgejoTreeEntryResponse> Tree);

    private sealed record ForgejoTreeEntryResponse(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("type")] string? Type);

    private sealed record ForgejoContentResponse(
        [property: JsonPropertyName("content")]
        string? Content,
        [property: JsonPropertyName("encoding")]
        string? Encoding);
}
