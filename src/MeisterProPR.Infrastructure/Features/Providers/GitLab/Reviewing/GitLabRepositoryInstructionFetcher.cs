// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Security;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.Providers.GitLab.Reviewing;

internal partial class GitLabRepositoryInstructionFetcher(
    IClientScmConnectionRepository connectionRepository,
    IHttpClientFactory httpClientFactory,
    ILogger<GitLabRepositoryInstructionFetcher> logger) : IProviderRepositoryInstructionFetcher
{
    private const string InstructionsFolder = ".meister-propr";
    private const string InstructionsFilePrefix = "instructions-";

    public ScmProvider Provider => ScmProvider.GitLab;

    public async Task<IReadOnlyList<RepositoryInstruction>> FetchAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        string targetBranch,
        Guid? clientId,
        CancellationToken cancellationToken)
    {
        LogFetchStarted(logger, repositoryId, targetBranch);

        IReadOnlyList<(string FileName, string Content)>? files;
        try
        {
            files = await this.FetchInstructionFilesAsync(
                organizationUrl,
                repositoryId,
                targetBranch,
                clientId,
                cancellationToken);
        }
        catch (Exception ex)
        {
            LogFetchFailed(logger, repositoryId, targetBranch, ex);
            return [];
        }

        if (files is null || files.Count == 0)
        {
            LogInstructionFolderAbsent(logger, repositoryId, targetBranch);
            return [];
        }

        var instructions = new List<RepositoryInstruction>();
        foreach (var (fileName, content) in files)
        {
            var instruction = RepositoryInstruction.Parse(fileName, content);
            if (instruction is not null)
            {
                instructions.Add(instruction);
            }
        }

        instructions.Sort((left, right) => string.Compare(
            left.FileName,
            right.FileName,
            StringComparison.OrdinalIgnoreCase));

        LogInstructionsFetched(logger, instructions.Count, repositoryId, targetBranch);
        return instructions.AsReadOnly();
    }

    protected virtual async Task<IReadOnlyList<(string FileName, string Content)>?> FetchInstructionFilesAsync(
        string organizationUrl,
        string repositoryId,
        string targetBranch,
        Guid? clientId,
        CancellationToken cancellationToken)
    {
        var host = new ProviderHostRef(ScmProvider.GitLab, organizationUrl);
        var connection = await this.GetConnectionAsync(host, clientId, cancellationToken);
        var normalizedBranch = NormalizeBranchName(targetBranch);
        var treeUri = GitLabConnectionVerifier.BuildApiUri(
            host,
            $"/projects/{Uri.EscapeDataString(repositoryId)}/repository/tree",
            $"path={Uri.EscapeDataString(InstructionsFolder)}&ref={Uri.EscapeDataString(normalizedBranch)}&per_page=100");

        using var treeRequest = GitLabConnectionVerifier.CreateAuthenticatedRequest(treeUri, connection.Secret);
        using var treeResponse =
            await httpClientFactory.CreateClient("GitLabProvider").SendAsync(treeRequest, cancellationToken);
        if (treeResponse.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        treeResponse.EnsureSuccessStatusCode();

        var items =
            await treeResponse.Content.ReadFromJsonAsync<IReadOnlyList<GitLabRepositoryTreeItem>>(cancellationToken)
            ?? [];
        if (items.Count == 0)
        {
            return null;
        }

        var results = new List<(string FileName, string Content)>();
        foreach (var item in items)
        {
            if (!string.Equals(item.Type, "blob", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(item.Path))
            {
                continue;
            }

            var fileName = Path.GetFileName(item.Path);
            if (!fileName.StartsWith(InstructionsFilePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var content = await this.TryReadFileAsync(
                host,
                connection.Secret,
                repositoryId,
                item.Path,
                normalizedBranch,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(content))
            {
                results.Add((fileName, content));
            }
        }

        return results.AsReadOnly();
    }

    private async Task<ClientScmConnectionCredentialDto> GetConnectionAsync(
        ProviderHostRef host,
        Guid? clientId,
        CancellationToken cancellationToken)
    {
        if (!clientId.HasValue)
        {
            throw new InvalidOperationException("GitLab repository instruction fetches require a client identifier.");
        }

        var connection =
            await connectionRepository.GetOperationalConnectionAsync(clientId.Value, host, cancellationToken)
            ?? throw new InvalidOperationException("No active GitLab connection is configured for the supplied host.");

        if (connection.AuthenticationKind != ScmAuthenticationKind.PersonalAccessToken)
        {
            throw new InvalidOperationException("GitLab repository instructions require personal access token authentication.");
        }

        return connection;
    }

    private async Task<string?> TryReadFileAsync(
        ProviderHostRef host,
        string token,
        string repositoryId,
        string path,
        string targetBranch,
        CancellationToken cancellationToken)
    {
        var fileUri = GitLabConnectionVerifier.BuildApiUri(
            host,
            $"/projects/{Uri.EscapeDataString(repositoryId)}/repository/files/{Uri.EscapeDataString(path)}/raw",
            $"ref={Uri.EscapeDataString(targetBranch)}");

        using var request = GitLabConnectionVerifier.CreateAuthenticatedRequest(fileUri, token);
        using var response =
            await httpClientFactory.CreateClient("GitLabProvider").SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static string NormalizeBranchName(string branch)
    {
        return branch.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)
            ? branch["refs/heads/".Length..]
            : branch;
    }

    [LoggerMessage(
        EventId = 4401,
        Level = LogLevel.Debug,
        Message = "Fetching repository instructions from {RepositoryId} on branch {Branch}")]
    private static partial void LogFetchStarted(ILogger logger, string repositoryId, string branch);

    [LoggerMessage(
        EventId = 4402,
        Level = LogLevel.Debug,
        Message = "Fetched {Count} relevant instruction(s) from {RepositoryId} on branch {Branch}")]
    private static partial void LogInstructionsFetched(ILogger logger, int count, string repositoryId, string branch);

    [LoggerMessage(
        EventId = 4403,
        Level = LogLevel.Debug,
        Message =
            "Instruction folder .meister-propr/ absent in {RepositoryId} on branch {Branch}; returning empty list")]
    private static partial void LogInstructionFolderAbsent(ILogger logger, string repositoryId, string branch);

    [LoggerMessage(
        EventId = 4404,
        Level = LogLevel.Warning,
        Message = "Failed to fetch repository instructions from {RepositoryId} on branch {Branch}")]
    private static partial void LogFetchFailed(ILogger logger, string repositoryId, string branch, Exception ex);

    private sealed record GitLabRepositoryTreeItem(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("type")] string? Type);
}
