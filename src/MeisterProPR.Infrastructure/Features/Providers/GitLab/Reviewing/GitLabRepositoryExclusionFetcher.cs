// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Security;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.Providers.GitLab.Reviewing;

internal partial class GitLabRepositoryExclusionFetcher(
    IClientScmConnectionRepository connectionRepository,
    IHttpClientFactory httpClientFactory,
    ILogger<GitLabRepositoryExclusionFetcher> logger) : IProviderRepositoryExclusionFetcher
{
    private const string ExcludeFilePath = ".meister-propr/exclude";

    public ScmProvider Provider => ScmProvider.GitLab;

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
        var host = new ProviderHostRef(ScmProvider.GitLab, organizationUrl);
        var connection = await this.GetConnectionAsync(host, clientId, cancellationToken);
        var normalizedBranch = NormalizeBranchName(targetBranch);
        var fileUri = GitLabConnectionVerifier.BuildApiUri(
            host,
            $"/projects/{Uri.EscapeDataString(repositoryId)}/repository/files/{Uri.EscapeDataString(ExcludeFilePath)}/raw",
            $"ref={Uri.EscapeDataString(normalizedBranch)}");

        using var request = GitLabConnectionVerifier.CreateAuthenticatedRequest(fileUri, connection.Secret);
        using var response =
            await httpClientFactory.CreateClient("GitLabProvider").SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<ClientScmConnectionCredentialDto> GetConnectionAsync(
        ProviderHostRef host,
        Guid? clientId,
        CancellationToken cancellationToken)
    {
        if (!clientId.HasValue)
        {
            throw new InvalidOperationException("GitLab repository exclusion fetches require a client identifier.");
        }

        var connection =
            await connectionRepository.GetOperationalConnectionAsync(clientId.Value, host, cancellationToken)
            ?? throw new InvalidOperationException("No active GitLab connection is configured for the supplied host.");

        if (connection.AuthenticationKind != ScmAuthenticationKind.PersonalAccessToken)
        {
            throw new InvalidOperationException("GitLab repository exclusions require personal access token authentication.");
        }

        return connection;
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
        EventId = 4501,
        Level = LogLevel.Debug,
        Message = "Fetching exclusion rules from {RepositoryId} on branch {Branch}")]
    private static partial void LogFetchStarted(ILogger logger, string repositoryId, string branch);

    [LoggerMessage(
        EventId = 4502,
        Level = LogLevel.Debug,
        Message = "Fetched {Count} exclusion pattern(s) from {RepositoryId} on branch {Branch}")]
    private static partial void LogPatternsFetched(ILogger logger, int count, string repositoryId, string branch);

    [LoggerMessage(
        EventId = 4503,
        Level = LogLevel.Debug,
        Message = ".meister-propr/exclude absent in {RepositoryId} on branch {Branch}; using default patterns")]
    private static partial void LogExcludeFileAbsent(ILogger logger, string repositoryId, string branch);

    [LoggerMessage(
        EventId = 4504,
        Level = LogLevel.Debug,
        Message =
            ".meister-propr/exclude in {RepositoryId} on branch {Branch} contains no usable patterns; using empty exclusion rules")]
    private static partial void LogNoPatternsParsed(ILogger logger, string repositoryId, string branch);

    [LoggerMessage(
        EventId = 4505,
        Level = LogLevel.Warning,
        Message = "Failed to fetch exclusion rules from {RepositoryId} on branch {Branch}")]
    private static partial void LogFetchFailed(ILogger logger, string repositoryId, string branch, Exception ex);
}
