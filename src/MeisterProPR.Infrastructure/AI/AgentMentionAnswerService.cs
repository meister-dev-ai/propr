using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using MeisterProPR.Domain.Interfaces;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     AI-backed implementation of <see cref="IMentionAnswerService" />.
///     Generates answers grounded in the pull request's diff, description, and existing threads.
/// </summary>
internal sealed partial class AgentMentionAnswerService(
    IChatClient chatClient,
    ILogger<AgentMentionAnswerService> logger) : IMentionAnswerService
{
    private const string SystemPrompt =
        "You are a PR review assistant. Answer the developer's question concisely and directly, " +
        "grounded only in the PR content provided. Do not initiate a full review. " +
        "If the question is about a specific line, focus your answer on that line and its immediate context. " +
        "Respond in plain text (markdown is fine) — no JSON.";

    private const int MaxFiles = 10;
    private const int MaxDiffLines = 200;
    private static readonly ActivitySource ActivitySource = new("MeisterProPR.Infrastructure");

    private static readonly Regex MentionPrefixRegex =
        new(
            @"^@<[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}>\s*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <inheritdoc />
    public async Task<string> AnswerAsync(
        PullRequest pullRequest,
        string question,
        int threadId,
        CancellationToken cancellationToken = default)
    {
        var cleanQuestion = MentionPrefixRegex.Replace(question, string.Empty).Trim();
        var userMessage = BuildUserMessage(pullRequest, cleanQuestion, threadId);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, userMessage),
        };

        using var activity = ActivitySource.StartActivity("AgentMentionAnswerService.Answer");
        activity?.SetTag("ai.pr_id", pullRequest.PullRequestId);
        activity?.SetTag("ai.thread_id", threadId);

        LogGeneratingAnswer(logger, pullRequest.PullRequestId, cleanQuestion.Length);

        var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
        return response.Text ?? "";
    }

    private static string BuildUserMessage(PullRequest pr, string question, int threadId)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"PR: {pr.Title}");

        if (!string.IsNullOrWhiteSpace(pr.Description))
        {
            sb.AppendLine($"Description: {pr.Description}");
        }

        if (pr.ChangedFiles.Count > 0)
        {
            var files = pr.ChangedFiles.Take(MaxFiles).ToList();
            sb.AppendLine();
            sb.AppendLine($"Changed files (showing {files.Count} of {pr.ChangedFiles.Count}):");

            foreach (var file in files)
            {
                sb.AppendLine();
                sb.AppendLine($"=== {file.Path} [{file.ChangeType}] ===");
                var diffLines = file.UnifiedDiff?.Split('\n') ?? [];
                var truncated = diffLines.Take(MaxDiffLines).ToArray();
                sb.AppendLine(string.Join('\n', truncated));
                if (diffLines.Length > MaxDiffLines)
                {
                    sb.AppendLine($"... ({diffLines.Length - MaxDiffLines} lines omitted)");
                }
            }
        }

        if (pr.ExistingThreads?.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Existing review threads:");
            foreach (var thread in pr.ExistingThreads)
            {
                sb.AppendLine($"  [{FormatThreadLocation(thread)}]");
                foreach (var comment in thread.Comments)
                {
                    sb.AppendLine($"    {comment.AuthorName}: {comment.Content}");
                }
            }
        }

        var focusThread = pr.ExistingThreads?.FirstOrDefault(t => t.ThreadId == threadId);
        if (focusThread is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"The following question was asked in a comment thread at: {FormatThreadLocation(focusThread)}");
            sb.AppendLine("Base your answer on the code at that location.");
        }

        sb.AppendLine();
        sb.AppendLine($"Question: {question}");
        return sb.ToString();
    }

    private static string FormatThreadLocation(PrCommentThread thread)
    {
        if (thread.FilePath is null)
        {
            return "(PR-level)";
        }

        return thread.LineNumber.HasValue
            ? $"{thread.FilePath}:L{thread.LineNumber}"
            : thread.FilePath;
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "AgentMentionAnswerService: generating answer for PR#{PullRequestId}, question length {QuestionLength}")]
    private static partial void LogGeneratingAnswer(ILogger logger, int pullRequestId, int questionLength);
}
