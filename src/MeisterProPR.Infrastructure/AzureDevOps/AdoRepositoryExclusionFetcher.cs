// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace MeisterProPR.Infrastructure.AzureDevOps;

/// <summary>
///     Azure DevOps-backed implementation of <see cref="IRepositoryExclusionFetcher" />.
///     Reads <c>.meister-propr/exclude</c> from the <b>target branch</b> of a repository
///     and parses it into a <see cref="ReviewExclusionRules" /> instance.
///     Files from the source branch are never read, preventing prompt injection via
///     attacker-controlled branches.
/// </summary>
public partial class AdoRepositoryExclusionFetcher(
    VssConnectionFactory connectionFactory,
    IClientAdoCredentialRepository credentialRepository,
    ILogger<AdoRepositoryExclusionFetcher> logger) : IRepositoryExclusionFetcher
{
    private const string ExcludeFilePath = "/.meister-propr/exclude";

    /// <inheritdoc />
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
            content = await this.FetchExcludeFileAsync(organizationUrl, projectId, repositoryId, targetBranch, clientId, cancellationToken);
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
            // File is present but contains no usable patterns — explicit empty rules (no exclusions).
            // Only a missing/unreadable file falls back to Default.
            return ReviewExclusionRules.Empty;
        }

        LogPatternsFetched(logger, patterns.Count, repositoryId, targetBranch);
        return ReviewExclusionRules.FromPatterns(patterns);
    }

    /// <summary>
    ///     Fetches the raw text content of <c>.meister-propr/exclude</c> from ADO.
    ///     Returns <see langword="null" /> when the file is absent.
    ///     Overridable in tests to inject controlled results without an ADO connection.
    /// </summary>
    protected virtual async Task<string?> FetchExcludeFileAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        string targetBranch,
        Guid? clientId,
        CancellationToken cancellationToken)
    {
        var gitClient = await this.GetGitClientAsync(organizationUrl, clientId, cancellationToken);

        var versionDescriptor = new GitVersionDescriptor
        {
            VersionType = GitVersionType.Branch,
            Version = NormalizeBranchName(targetBranch),
        };

        GitItem? fileItem;
        try
        {
            fileItem = await gitClient.GetItemAsync(
                projectId,
                repositoryId,
                ExcludeFilePath,
                null,
                null,
                null,
                null,
                null,
                versionDescriptor,
                true, // includeContent
                null,
                null,
                null,
                cancellationToken);
        }
        catch (Exception ex)
        {
            // Log at debug — may be a genuine not-found/permission error or a transient network issue.
            // Either way, treat as absent so a fetch failure never blocks a review.
            logger.LogDebug(ex, "Failed to fetch .meister-propr/exclude from {RepositoryId}@{Branch}; treating as absent", repositoryId, targetBranch);
            return null;
        }

        // Return the raw content (possibly empty string) so FetchAsync can distinguish
        // "file absent" (null) from "file present with no usable patterns" (empty → Empty rules).
        return fileItem?.Content ?? string.Empty;
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

    /// <summary>Strips <c>refs/heads/</c> prefix from a branch name so it matches what ADO's branch-scoped APIs expect.</summary>
    private static string NormalizeBranchName(string branch) =>
        branch.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)
            ? branch["refs/heads/".Length..]
            : branch;

    private async Task<GitHttpClient> GetGitClientAsync(string organizationUrl, Guid? clientId, CancellationToken ct)
    {
        var credentials = clientId.HasValue
            ? await credentialRepository.GetByClientIdAsync(clientId.Value, ct)
            : null;
        var connection = await connectionFactory.GetConnectionAsync(organizationUrl, credentials, ct);
        return connection.GetClient<GitHttpClient>();
    }

    [LoggerMessage(EventId = 4101, Level = LogLevel.Debug, Message = "Fetching exclusion rules from {RepositoryId} on branch {Branch}")]
    private static partial void LogFetchStarted(ILogger logger, string repositoryId, string branch);

    [LoggerMessage(EventId = 4102, Level = LogLevel.Debug, Message = "Fetched {Count} exclusion pattern(s) from {RepositoryId} on branch {Branch}")]
    private static partial void LogPatternsFetched(ILogger logger, int count, string repositoryId, string branch);

    [LoggerMessage(
        EventId = 4103,
        Level = LogLevel.Debug,
        Message = ".meister-propr/exclude absent in {RepositoryId} on branch {Branch}; using default patterns")]
    private static partial void LogExcludeFileAbsent(ILogger logger, string repositoryId, string branch);

    [LoggerMessage(
        EventId = 4104,
        Level = LogLevel.Debug,
        Message = ".meister-propr/exclude in {RepositoryId} on branch {Branch} contains no usable patterns; using empty exclusion rules")]
    private static partial void LogNoPatternsParsed(ILogger logger, string repositoryId, string branch);

    [LoggerMessage(EventId = 4105, Level = LogLevel.Warning, Message = "Failed to fetch exclusion rules from {RepositoryId} on branch {Branch}")]
    private static partial void LogFetchFailed(ILogger logger, string repositoryId, string branch, Exception ex);
}
