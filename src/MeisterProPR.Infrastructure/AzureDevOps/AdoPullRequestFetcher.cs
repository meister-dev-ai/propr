using System.Text;
using DiffPlex.DiffBuilder;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace MeisterProPR.Infrastructure.AzureDevOps;

public partial class AdoPullRequestFetcher(
    VssConnectionFactory connectionFactory,
    IClientAdoCredentialRepository credentialRepository,
    ILogger<AdoPullRequestFetcher> logger) : IPullRequestFetcher
{
    public async Task<PullRequest> FetchAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        var credentials = clientId.HasValue
            ? await credentialRepository.GetByClientIdAsync(clientId.Value, cancellationToken)
            : null;
        var connection = await connectionFactory.GetConnectionAsync(organizationUrl, credentials, cancellationToken);
        var gitClient = connection.GetClient<GitHttpClient>();

        // Get PR metadata
        var pr = await gitClient.GetPullRequestAsync(
            projectId,
            repositoryId,
            pullRequestId,
            cancellationToken: cancellationToken);

        // Get iteration for source/base commit SHAs
        var iteration = await gitClient.GetPullRequestIterationAsync(
            projectId,
            repositoryId,
            pullRequestId,
            iterationId,
            cancellationToken: cancellationToken);

        var sourceCommit = iteration.SourceRefCommit?.CommitId
                           ?? pr.LastMergeSourceCommit?.CommitId ?? "";
        var baseCommit = iteration.CommonRefCommit?.CommitId
                         ?? pr.LastMergeTargetCommit?.CommitId ?? "";

        // Get changed files
        var changes = await gitClient.GetPullRequestIterationChangesAsync(
            projectId,
            repositoryId,
            pullRequestId,
            iterationId,
            cancellationToken: cancellationToken);

        var changedFiles = new List<ChangedFile>();
        foreach (var change in changes.ChangeEntries ?? [])
        {
            if (change.Item?.IsFolder == true)
            {
                continue;
            }

            var path = change.Item?.Path ?? "";
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            var changeType = change.ChangeType switch
            {
                VersionControlChangeType.Add => ChangeType.Add,
                VersionControlChangeType.Edit => ChangeType.Edit,
                VersionControlChangeType.Delete => ChangeType.Delete,
                _ => ChangeType.Edit,
            };

            var headContent = "";
            var baseContent = "";

            if (changeType != ChangeType.Delete && !string.IsNullOrEmpty(sourceCommit))
            {
                try
                {
                    var item = await gitClient.GetItemAsync(
                        projectId,
                        repositoryId,
                        path,
                        null, // scopePath
                        null, // recursionLevel
                        null, // includeContentMetadata
                        null, // latestProcessedChange
                        null, // download
                        new GitVersionDescriptor
                        {
                            VersionType = GitVersionType.Commit,
                            Version = sourceCommit,
                        },
                        true, // includeContent
                        null, // resolveLfs
                        null, // sanitize
                        null, // userState
                        cancellationToken);
                    headContent = item.Content ?? "";
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to fetch head content for {Path} at commit {Commit}", path, sourceCommit);
                    headContent = "";
                }
            }

            if (changeType != ChangeType.Add && !string.IsNullOrEmpty(baseCommit))
            {
                try
                {
                    var item = await gitClient.GetItemAsync(
                        projectId,
                        repositoryId,
                        path,
                        null, // scopePath
                        null, // recursionLevel
                        null, // includeContentMetadata
                        null, // latestProcessedChange
                        null, // download
                        new GitVersionDescriptor
                        {
                            VersionType = GitVersionType.Commit,
                            Version = baseCommit,
                        },
                        true, // includeContent
                        null, // resolveLfs
                        null, // sanitize
                        null, // userState
                        cancellationToken);
                    baseContent = item.Content ?? "";
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to fetch base content for {Path} at commit {Commit}", path, baseCommit);
                    baseContent = "";
                }
            }

            var diff = BuildUnifiedDiff(baseContent, headContent);
            changedFiles.Add(new ChangedFile(path, changeType, headContent, diff));
        }

        var existingThreads = await this.FetchExistingThreadsAsync(gitClient, projectId, repositoryId, pullRequestId, cancellationToken);

        return new PullRequest(
            organizationUrl,
            projectId,
            repositoryId,
            pullRequestId,
            iterationId,
            pr.Title ?? "",
            pr.Description,
            pr.SourceRefName ?? "",
            pr.TargetRefName ?? "",
            changedFiles.AsReadOnly(),
            ExistingThreads: existingThreads);
    }

    private static string BuildUnifiedDiff(string oldContent, string newContent)
    {
        var diff = InlineDiffBuilder.Diff(oldContent, newContent);
        var sb = new StringBuilder();
        foreach (var line in diff.Lines)
        {
            var prefix = line.Type switch
            {
                DiffPlex.DiffBuilder.Model.ChangeType.Inserted => "+ ",
                DiffPlex.DiffBuilder.Model.ChangeType.Deleted => "- ",
                _ => "  ",
            };
            sb.AppendLine($"{prefix}{line.Text}");
        }

        return sb.ToString();
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to fetch existing comment threads for PR #{PullRequestId}. Proceeding without thread context.")]
    private static partial void LogThreadFetchWarning(ILogger logger, int pullRequestId, Exception ex);

    private async Task<IReadOnlyList<PrCommentThread>> FetchExistingThreadsAsync(
        GitHttpClient gitClient,
        string projectId,
        string repositoryId,
        int pullRequestId,
        CancellationToken cancellationToken)
    {
        try
        {
            var rawThreads = await gitClient.GetThreadsAsync(
                projectId,
                repositoryId,
                pullRequestId,
                cancellationToken: cancellationToken);

            return (rawThreads ?? [])
                .Where(t => !t.IsDeleted && t.Comments?.Count > 0)
                .Select(t => new PrCommentThread(
                    t.Id,
                    t.ThreadContext?.FilePath,
                    t.ThreadContext?.RightFileStart?.Line,
                    t.Comments!
                        .Where(c => !c.IsDeleted)
                        .Select(c => new PrThreadComment(
                            c.Author?.DisplayName ?? "Unknown",
                            c.Content ?? "",
                            Guid.TryParse(c.Author?.Id, out var aid) ? aid : null,
                            c.Id,
                            c.PublishedDate != default
                                ? new DateTimeOffset(c.PublishedDate, TimeSpan.Zero)
                                : null))
                        .ToList()
                        .AsReadOnly()))
                .ToList()
                .AsReadOnly();
        }
        catch (Exception ex)
        {
            LogThreadFetchWarning(logger, pullRequestId, ex);
            return [];
        }
    }
}