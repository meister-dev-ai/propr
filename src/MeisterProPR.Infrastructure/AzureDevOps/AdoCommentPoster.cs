using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace MeisterProPR.Infrastructure.AzureDevOps;

public sealed class AdoCommentPoster(
    VssConnectionFactory connectionFactory,
    IClientAdoCredentialRepository credentialRepository) : IAdoCommentPoster
{
    public async Task PostAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        ReviewResult result,
        Guid? clientId = null,
        IReadOnlyList<PrCommentThread>? existingThreads = null,
        CancellationToken cancellationToken = default)
    {
        var credentials = clientId.HasValue
            ? await credentialRepository.GetByClientIdAsync(clientId.Value, cancellationToken)
            : null;
        var connection = await connectionFactory.GetConnectionAsync(organizationUrl, credentials, cancellationToken);
        await connection.ConnectAsync(cancellationToken: cancellationToken);
        var botId = connection.AuthorizedIdentity?.Id;
        var gitClient = connection.GetClient<GitHttpClient>();

        // Build a map of normalised file path → changeTrackingId for inline comment anchoring.
        // changeTrackingId is required by ADO to resolve a file thread against the correct diff.
        var changes = await gitClient.GetPullRequestIterationChangesAsync(
            projectId,
            repositoryId,
            pullRequestId,
            iterationId,
            cancellationToken: cancellationToken);
        var changeTrackingIds = (changes.ChangeEntries ?? [])
            .Where(c => c.Item?.Path is not null)
            .ToDictionary(
                c => NormalizePath(c.Item!.Path!),
                c => c.ChangeTrackingId);

        // Post summary as PR-level thread, skipping if a bot summary already exists.
        if (!HasBotSummary(existingThreads, botId))
        {
            await CreateThreadAsync(
                gitClient,
                projectId,
                repositoryId,
                pullRequestId,
                $"**AI Review Summary**\n\n{result.Summary}",
                null,
                null,
                cancellationToken);
        }

        // Post each inline comment, skipping locations the bot has already covered.
        foreach (var comment in result.Comments)
        {
            CommentThreadContext? threadContext = null;
            GitPullRequestCommentThreadContext? prThreadContext = null;
            string? normalizedFilePath = null;

            if (comment.FilePath is not null)
            {
                // ADO requires paths with a leading '/'; normalise in case the AI omits it.
                normalizedFilePath = NormalizePath(comment.FilePath);
                threadContext = new CommentThreadContext
                {
                    FilePath = normalizedFilePath,
                    RightFileStart = comment.LineNumber.HasValue
                        ? new CommentPosition { Line = comment.LineNumber.Value, Offset = 1 }
                        : null,
                    RightFileEnd = comment.LineNumber.HasValue
                        ? new CommentPosition { Line = comment.LineNumber.Value, Offset = 1 }
                        : null,
                };

                // pullRequestThreadContext anchors the thread to the correct iteration diff.
                if (changeTrackingIds.TryGetValue(normalizedFilePath, out var trackingId))
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

            if (HasBotThreadAt(existingThreads, normalizedFilePath, comment.LineNumber, botId))
            {
                continue;
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

    /// <summary>
    ///     Returns <c>true</c> if a bot-authored PR-level summary thread already exists.
    ///     Bot authorship is determined by comparing the comment's <see cref="PrThreadComment.AuthorId" />
    ///     against the current connection's authorized identity (<paramref name="botId" />).
    /// </summary>
    internal static bool HasBotSummary(IReadOnlyList<PrCommentThread>? threads, Guid? botId)
    {
        return (threads ?? []).Any(t =>
            t.FilePath is null &&
            t.Comments.Any(c => IsBotAuthor(c.AuthorId, botId)
                                && c.Content.StartsWith("**AI Review Summary**", StringComparison.Ordinal)));
    }

    /// <summary>
    ///     Returns <c>true</c> if a bot-authored thread already exists at the given file path and line number.
    ///     Bot authorship is determined by comparing the comment's <see cref="PrThreadComment.AuthorId" />
    ///     against the current connection's authorized identity (<paramref name="botId" />).
    /// </summary>
    internal static bool HasBotThreadAt(
        IReadOnlyList<PrCommentThread>? threads,
        string? filePath,
        int? lineNumber,
        Guid? botId)
    {
        return filePath is not null &&
               (threads ?? []).Any(t =>
                   t.FilePath == filePath &&
                   t.LineNumber == lineNumber &&
                   t.Comments.Any(c => IsBotAuthor(c.AuthorId, botId)));
    }

    /// <summary>
    ///     Returns <c>true</c> if the comment was authored by the bot, identified by VSS identity GUID equality.
    ///     Returns <c>false</c> if either GUID is unknown.
    /// </summary>
    internal static bool IsBotAuthor(Guid? authorId, Guid? botId)
    {
        return authorId.HasValue && botId.HasValue && authorId.Value == botId.Value;
    }

    /// <summary>Maximum number of characters allowed in a single ADO PR comment to stay safely below API limits.</summary>
    internal const int MaxCommentLength = 30_000;

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
        var content = TruncateIfNeeded(message);
        var thread = new GitPullRequestCommentThread
        {
            Comments = [new Comment { Content = content, CommentType = CommentType.Text }],
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

    internal static string TruncateIfNeeded(string message)
    {
        if (message.Length <= MaxCommentLength)
        {
            return message;
        }

        const string notice = "\n\n> *(Review comment truncated — view the full review in the MeisterProPR admin UI)*";
        var cutoff = MaxCommentLength - notice.Length;

        // Trim to last whitespace boundary so we don't cut mid-word.
        var boundary = message.LastIndexOf(' ', cutoff);
        if (boundary < 1)
        {
            boundary = cutoff;
        }

        return message[..boundary] + notice;
    }

    private static string NormalizePath(string path)
    {
        return path.StartsWith('/') ? path : "/" + path;
    }
}
