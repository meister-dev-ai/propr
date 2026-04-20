// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Security;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

internal partial class GitHubRepositoryExclusionFetcher(
    IClientScmConnectionRepository connectionRepository,
    IHttpClientFactory httpClientFactory,
    ILogger<GitHubRepositoryExclusionFetcher> logger) : IProviderRepositoryExclusionFetcher
{
    private const string ExcludeFilePath = ".meister-propr/exclude";

    public ScmProvider Provider => ScmProvider.GitHub;

    public async Task<ReviewExclusionRules> FetchAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        string targetBranch,
        Guid? clientId,
        CancellationToken cancellationToken)
    {
        LogFetchStarted(logger, repositoryId, targetBranch);

        string? content;
        try
        {
            content = await this.FetchExcludeFileAsync(
                organizationUrl,
                repositoryId,
                targetBranch,
                clientId,
                cancellationToken);
        }
        catch (Exception ex)
        {
            LogFetchFailed(logger, repositoryId, targetBranch, ex);
            return ReviewExclusionRules.Default;
        }

        if (content is null)
        {
            LogExcludeFileAbsent(logger, repositoryId, targetBranch);
            return ReviewExclusionRules.Default;
        }

        var patterns = ParsePatterns(content);
        if (patterns.Count == 0)
        {
            LogNoPatternsParsed(logger, repositoryId, targetBranch);
            return ReviewExclusionRules.Empty;
        }

        LogPatternsFetched(logger, patterns.Count, repositoryId, targetBranch);
        return ReviewExclusionRules.FromPatterns(patterns);
    }

    protected virtual async Task<string?> FetchExcludeFileAsync(
        string organizationUrl,
        string repositoryId,
        string targetBranch,
        Guid? clientId,
        CancellationToken cancellationToken)
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, organizationUrl);
        var connection = await this.GetConnectionAsync(host, clientId, cancellationToken);
        var repositoryPath = await this.ResolveRepositoryPathAsync(
            host,
            repositoryId,
            connection.Secret,
            cancellationToken);
        var normalizedBranch = NormalizeBranchName(targetBranch);
        var fileUri = GitHubConnectionVerifier.BuildApiUri(
            host,
            $"/repos/{repositoryPath}/contents/{Uri.EscapeDataString(ExcludeFilePath)}",
            $"ref={Uri.EscapeDataString(normalizedBranch)}");

        using var request = GitHubConnectionVerifier.CreateAuthenticatedRequest(fileUri, connection.Secret);
        using var response =
            await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<GitHubContentResponse>(cancellationToken)
                      ?? throw new InvalidOperationException("GitHub exclusion lookup returned an empty payload.");
        if (string.Equals(payload.Encoding, "base64", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(payload.Content))
        {
            var raw = payload.Content.Replace("\n", string.Empty, StringComparison.Ordinal);
            return Encoding.UTF8.GetString(Convert.FromBase64String(raw));
        }

        return payload.Content;
    }

    private async Task<ClientScmConnectionCredentialDto> GetConnectionAsync(
        ProviderHostRef host,
        Guid? clientId,
        CancellationToken cancellationToken)
    {
        if (!clientId.HasValue)
        {
            throw new InvalidOperationException("GitHub repository exclusion fetches require a client identifier.");
        }

        var connection =
            await connectionRepository.GetOperationalConnectionAsync(clientId.Value, host, cancellationToken)
            ?? throw new InvalidOperationException("No active GitHub connection is configured for the supplied host.");

        if (connection.AuthenticationKind != ScmAuthenticationKind.PersonalAccessToken)
        {
            throw new InvalidOperationException("GitHub repository exclusions require personal access token authentication.");
        }

        return connection;
    }

    private async Task<string> ResolveRepositoryPathAsync(
        ProviderHostRef host,
        string repositoryId,
        string secret,
        CancellationToken cancellationToken)
    {
        using var request = GitHubConnectionVerifier.CreateAuthenticatedRequest(
            GitHubConnectionVerifier.BuildApiUri(host, $"/repositories/{Uri.EscapeDataString(repositoryId)}"),
            secret);
        using var response =
            await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub repository lookup failed with status {(int)response.StatusCode}.");
        }

        var repository = await response.Content.ReadFromJsonAsync<GitHubRepositoryResponse>(cancellationToken)
                         ?? throw new InvalidOperationException("GitHub repository lookup returned an empty payload.");
        if (string.IsNullOrWhiteSpace(repository.FullName))
        {
            throw new InvalidOperationException("GitHub repository lookup did not return a repository full name.");
        }

        return repository.FullName.Trim();
    }

    private static IReadOnlyList<string> ParsePatterns(string content)
    {
        var patterns = new List<string>();
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
            {
                continue;
            }

            patterns.Add(line);
        }

        return patterns.AsReadOnly();
    }

    private static string NormalizeBranchName(string branch)
    {
        return branch.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)
            ? branch["refs/heads/".Length..]
            : branch;
    }

    [LoggerMessage(
        EventId = 4701,
        Level = LogLevel.Debug,
        Message = "Fetching exclusion rules from {RepositoryId} on branch {Branch}")]
    private static partial void LogFetchStarted(ILogger logger, string repositoryId, string branch);

    [LoggerMessage(
        EventId = 4702,
        Level = LogLevel.Debug,
        Message = "Fetched {Count} exclusion pattern(s) from {RepositoryId} on branch {Branch}")]
    private static partial void LogPatternsFetched(ILogger logger, int count, string repositoryId, string branch);

    [LoggerMessage(
        EventId = 4703,
        Level = LogLevel.Debug,
        Message = ".meister-propr/exclude absent in {RepositoryId} on branch {Branch}; using default patterns")]
    private static partial void LogExcludeFileAbsent(ILogger logger, string repositoryId, string branch);

    [LoggerMessage(
        EventId = 4704,
        Level = LogLevel.Debug,
        Message =
            ".meister-propr/exclude in {RepositoryId} on branch {Branch} contains no usable patterns; using empty exclusion rules")]
    private static partial void LogNoPatternsParsed(ILogger logger, string repositoryId, string branch);

    [LoggerMessage(
        EventId = 4705,
        Level = LogLevel.Warning,
        Message = "Failed to fetch exclusion rules from {RepositoryId} on branch {Branch}")]
    private static partial void LogFetchFailed(ILogger logger, string repositoryId, string branch, Exception ex);

    private sealed record GitHubRepositoryResponse(
        [property: JsonPropertyName("full_name")]
        string? FullName);

    private sealed record GitHubContentResponse(
        [property: JsonPropertyName("content")]
        string? Content,
        [property: JsonPropertyName("encoding")]
        string? Encoding);
}
