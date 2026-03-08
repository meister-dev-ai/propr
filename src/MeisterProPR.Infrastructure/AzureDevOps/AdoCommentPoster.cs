using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace MeisterProPR.Infrastructure.AzureDevOps;

public sealed class AdoCommentPoster(VssConnectionFactory connectionFactory) : IAdoCommentPoster
{
    public async Task PostAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        ReviewResult result,
        CancellationToken cancellationToken = default)
    {
        var connection = await connectionFactory.GetConnectionAsync(organizationUrl, cancellationToken);
        var gitClient = connection.GetClient<GitHttpClient>();

        // Build a map of normalised file path → changeTrackingId for inline comment anchoring.
        // changeTrackingId is required by ADO to resolve a file thread against the correct diff.
        var changes = await gitClient.GetPullRequestIterationChangesAsync(
            projectId, repositoryId, pullRequestId, iterationId,
            cancellationToken: cancellationToken);
        var changeTrackingIds = (changes.ChangeEntries ?? [])
            .Where(c => c.Item?.Path is not null)
            .ToDictionary(
                c => NormalizePath(c.Item!.Path!),
                c => c.ChangeTrackingId);

        // Post summary as PR-level thread (no file context)
        await CreateThreadAsync(
            gitClient,
            projectId,
            repositoryId,
            pullRequestId,
            $"**AI Review Summary**\n\n{result.Summary}",
            null,
            null,
            cancellationToken);

        // Post each inline comment
        foreach (var comment in result.Comments)
        {
            CommentThreadContext? threadContext = null;
            GitPullRequestCommentThreadContext? prThreadContext = null;

            if (comment.FilePath is not null)
            {
                // ADO requires paths with a leading '/'; normalise in case the AI omits it.
                var filePath = NormalizePath(comment.FilePath);
                threadContext = new CommentThreadContext
                {
                    FilePath = filePath,
                    RightFileStart = comment.LineNumber.HasValue
                        ? new CommentPosition { Line = comment.LineNumber.Value, Offset = 1 }
                        : null,
                    RightFileEnd = comment.LineNumber.HasValue
                        ? new CommentPosition { Line = comment.LineNumber.Value, Offset = 1 }
                        : null,
                };

                // pullRequestThreadContext anchors the thread to the correct iteration diff.
                if (changeTrackingIds.TryGetValue(filePath, out var trackingId))
                {
                    prThreadContext = new GitPullRequestCommentThreadContext
                    {
                        ChangeTrackingId = trackingId,
                        IterationContext = new CommentIterationContext
                        {
                            FirstComparingIteration = (short)iterationId,
                            SecondComparingIteration = (short)iterationId,
                        },
                    };
                }
            }

            var severityPrefix = comment.Severity switch
            {
                CommentSeverity.Error => "ERROR",
                CommentSeverity.Warning => "WARNING",
                CommentSeverity.Suggestion => "SUGGESTION",
                _ => "INFO",
            };

            await CreateThreadAsync(
                gitClient,
                projectId,
                repositoryId,
                pullRequestId,
                $"{severityPrefix}: {comment.Message}",
                threadContext,
                prThreadContext,
                cancellationToken);
        }
    }

    private static string NormalizePath(string path) =>
        path.StartsWith('/') ? path : "/" + path;

    private static async Task CreateThreadAsync(
        GitHttpClient gitClient,
        string projectId,
        string repositoryId,
        int pullRequestId,
        string message,
        CommentThreadContext? threadContext,
        GitPullRequestCommentThreadContext? prThreadContext,
        CancellationToken ct)
    {
        var thread = new GitPullRequestCommentThread
        {
            Comments = [new Comment { Content = message, CommentType = CommentType.Text }],
            Status = CommentThreadStatus.Active,
            ThreadContext = threadContext,
            PullRequestThreadContext = prThreadContext,
        };
        await gitClient.CreateThreadAsync(
            thread,
            repositoryId,
            pullRequestId,
            projectId,
            ct);
    }
}
