// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

/// <summary>
///     Azure DevOps-backed implementation of <see cref="IRepositoryInstructionFetcher" />.
///     Reads <c>.meister-propr/instructions-*.md</c> files from the <b>target branch</b> of a
///     repository and parses them into <see cref="RepositoryInstruction" /> objects.
///     Files from the source branch are never read, preventing prompt injection via
///     attacker-controlled branches.
/// </summary>
public partial class AdoRepositoryInstructionFetcher(
    VssConnectionFactory connectionFactory,
    IClientScmConnectionRepository connectionRepository,
    ILogger<AdoRepositoryInstructionFetcher> logger)
    : IRepositoryInstructionFetcher, IProviderRepositoryInstructionFetcher
{
    private const string InstructionsFolder = ".meister-propr";
    private const string InstructionsFilePrefix = "instructions-";

    public ScmProvider Provider => ScmProvider.AzureDevOps;

    /// <inheritdoc />
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
                projectId,
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

        instructions.Sort((a, b) => string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase));

        LogInstructionsFetched(logger, instructions.Count, repositoryId, targetBranch);
        return instructions.AsReadOnly();
    }

    /// <summary>
    ///     Fetches instruction file names and their raw text content from ADO.
    ///     Returns <see langword="null" /> when the <c>.meister-propr/</c> folder is absent.
    ///     Overridable in tests to inject controlled results without an ADO connection.
    /// </summary>
    /// <param name="organizationUrl">Azure DevOps organization URL.</param>
    /// <param name="projectId">Azure DevOps project identifier.</param>
    /// <param name="repositoryId">Repository identifier.</param>
    /// <param name="targetBranch">Branch to read instructions from.</param>
    /// <param name="clientId">Optional client identifier for credential lookup.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of (fileName, content) tuples, or <see langword="null" /> if folder absent.</returns>
    protected virtual async Task<IReadOnlyList<(string FileName, string Content)>?> FetchInstructionFilesAsync(
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
            Version = targetBranch,
        };

        List<GitItem>? items;
        try
        {
            items = await gitClient.GetItemsAsync(
                projectId,
                repositoryId,
                $"/{InstructionsFolder}",
                VersionControlRecursionType.OneLevel,
                versionDescriptor: versionDescriptor,
                userState: null,
                cancellationToken: cancellationToken);
        }
        catch
        {
            // Folder not found or access denied — treat as absent
            return null;
        }

        if (items is null || items.Count == 0)
        {
            return null;
        }

        var results = new List<(string FileName, string Content)>();
        foreach (var item in items)
        {
            var path = item.Path ?? "";
            if (item.IsFolder || string.IsNullOrEmpty(path))
            {
                continue;
            }

            var fileName = Path.GetFileName(path);
            if (!fileName.StartsWith(InstructionsFilePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            GitItem? fileItem;
            try
            {
                fileItem = await gitClient.GetItemAsync(
                    projectId,
                    repositoryId,
                    path,
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
            catch
            {
                continue;
            }

            var content = fileItem?.Content;
            if (!string.IsNullOrWhiteSpace(content))
            {
                results.Add((fileName, content));
            }
        }

        return results.AsReadOnly();
    }

    private async Task<GitHttpClient> GetGitClientAsync(string organizationUrl, Guid? clientId, CancellationToken ct)
    {
        var credentials = await AdoProviderAdapterHelpers.ResolveCredentialsAsync(
            connectionRepository,
            clientId,
            organizationUrl,
            ct);
        var connection = await connectionFactory.GetConnectionAsync(organizationUrl, credentials, ct);
        return connection.GetClient<GitHttpClient>();
    }

    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Debug,
        Message = "Fetching repository instructions from {RepositoryId} on branch {Branch}")]
    private static partial void LogFetchStarted(ILogger logger, string repositoryId, string branch);

    [LoggerMessage(
        EventId = 4002,
        Level = LogLevel.Debug,
        Message = "Fetched {Count} relevant instruction(s) from {RepositoryId} on branch {Branch}")]
    private static partial void LogInstructionsFetched(ILogger logger, int count, string repositoryId, string branch);

    [LoggerMessage(
        EventId = 4003,
        Level = LogLevel.Debug,
        Message =
            "Instruction folder .meister-propr/ absent in {RepositoryId} on branch {Branch}; returning empty list")]
    private static partial void LogInstructionFolderAbsent(ILogger logger, string repositoryId, string branch);

    [LoggerMessage(
        EventId = 4004,
        Level = LogLevel.Warning,
        Message = "Failed to fetch repository instructions from {RepositoryId} on branch {Branch}")]
    private static partial void LogFetchFailed(ILogger logger, string repositoryId, string branch, Exception ex);
}
