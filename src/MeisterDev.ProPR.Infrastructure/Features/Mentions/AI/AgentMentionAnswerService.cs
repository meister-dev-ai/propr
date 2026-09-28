// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Interfaces;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.AI;

/// <summary>
///     AI-backed implementation of <see cref="IMentionAnswerService" />.
///     Generates answers grounded in the pull request's diff, description, and existing threads.
/// </summary>
internal sealed partial class AgentMentionAnswerService(
    IAiRuntimeResolver aiRuntimeResolver,
    ILogger<AgentMentionAnswerService> logger,
    IClientRegistry? clientRegistry = null) : IMentionAnswerService
{
    private const string SystemPrompt =
        "You are a PR review assistant. Answer the developer's question concisely and directly, " +
        "grounded only in the PR content provided. Do not initiate a full review. " +
        "If the question is about a specific line, focus your answer on that line and its immediate context. " +
        "Respond in plain text (markdown is fine). Do not return JSON. " +
        "A diff omitted from this request does not establish that a file is absent from the PR. " +
        "State when the provided context is unavailable or insufficient.";

    private const int MaxFiles = 10;
    private const int MaxDiffLines = 200;

    private static readonly Regex HunkHeaderRegex = new(
        @"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly ActivitySource ActivitySource = new("MeisterProPR.Infrastructure");

    private static readonly Regex MentionPrefixRegex =
        new(
            @"^@<[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}>\s*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromSeconds(1));

    /// <inheritdoc />
    public async Task<MentionAnswer> AnswerAsync(
        PullRequest pullRequest,
        Guid clientId,
        string question,
        string threadId,
        CancellationToken cancellationToken = default)
    {
        var cleanQuestion = MentionPrefixRegex.Replace(question, string.Empty).Trim();
        var userMessage = BuildUserMessage(pullRequest, cleanQuestion, threadId);

        var runtime = await aiRuntimeResolver.ResolveChatRuntimeAsync(clientId, AiPurpose.ReviewDefault, cancellationToken);
        var modelId = runtime.Model.RemoteModelId;
        var chatClient = runtime.ChatClient;
        Guid? connectionId = runtime.Connection.Id;
        var logicalModelName = runtime.LogicalModelName;

        // Mention answers resolve the output language from the client because they have no review job.
        var outputLanguage = clientRegistry is null
            ? null
            : await clientRegistry.GetOutputLanguageAsync(clientId, cancellationToken);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, OutputLanguageDirective.Append(SystemPrompt, outputLanguage)),
            new(ChatRole.User, userMessage),
        };

        using var activity = ActivitySource.StartActivity("AgentMentionAnswerService.Answer");
        activity?.SetTag("ai.pr_id", pullRequest.PullRequestId);
        activity?.SetTag("ai.thread_id", threadId);

        LogGeneratingAnswer(logger, pullRequest.PullRequestId, cleanQuestion.Length);

        var response = await chatClient.GetResponseAsync(
            messages,
            new ChatOptions { ModelId = modelId },
            cancellationToken);

        // The orchestrator records and prices the usage returned with the answer.
        return new MentionAnswer(
            response.Text ?? string.Empty,
            AiTokenUsageExtractor.FromResponse(response),
            modelId,
            connectionId,
            logicalModelName);
    }

    private static string BuildUserMessage(PullRequest pr, string question, string threadId)
    {
        var sb = new StringBuilder();
        var focusThread = pr.ExistingThreads?.FirstOrDefault(t =>
            string.Equals(t.ThreadId, threadId, StringComparison.Ordinal));
        var focusFile = focusThread?.FilePath is { } path
            ? pr.ChangedFiles.FirstOrDefault(file => string.Equals(file.Path.TrimStart('/'), path.TrimStart('/'), StringComparison.Ordinal))
            : null;
        sb.AppendLine($"PR: {pr.Title}");

        if (!string.IsNullOrWhiteSpace(pr.Description))
        {
            sb.AppendLine($"Description: {pr.Description}");
        }

        if (pr.ChangedFiles.Count > 0)
        {
            var files = pr.ChangedFiles.OrderByDescending(file => ReferenceEquals(file, focusFile)).Take(MaxFiles).ToList();
            sb.AppendLine();
            sb.AppendLine($"Changed files (showing {files.Count} of {pr.ChangedFiles.Count}):");

            foreach (var file in files)
            {
                sb.AppendLine();
                sb.AppendLine($"=== {file.Path} [{file.ChangeType}] ===");
                if (file.IsBinary || string.IsNullOrWhiteSpace(file.UnifiedDiff))
                {
                    sb.AppendLine(
                        ReferenceEquals(file, focusFile)
                            ? "Referenced file diff is unavailable."
                            : "File diff is unavailable.");
                    continue;
                }

                var diffLines = file.UnifiedDiff.Split('\n');
                var location = ReferenceEquals(file, focusFile) && focusThread?.LineNumber is { } line
                    ? FindDiffLocation(diffLines, line)
                    : null;
                if (ReferenceEquals(file, focusFile) && focusThread?.LineNumber is not null && location is null)
                {
                    sb.AppendLine("Referenced line is unavailable in the provided diff.");
                }

                var start = location is { } found ? Math.Max(found.Index - MaxDiffLines / 2, found.HunkStart) : 0;
                var includeHeader = location is { } hunk && start > hunk.HunkStart;
                if (includeHeader)
                {
                    sb.AppendLine($"Diff excerpt omits {start - location!.Value.HunkStart - 1} rows after this hunk header before the displayed rows.");
                    sb.AppendLine(diffLines[location!.Value.HunkStart]);
                }

                var window = diffLines.Skip(start).Take(MaxDiffLines - (includeHeader ? 1 : 0)).ToArray();
                sb.AppendLine(string.Join('\n', window));
                if (start > 0 || start + window.Length < diffLines.Length)
                {
                    sb.AppendLine($"... ({diffLines.Length - window.Length - (includeHeader ? 1 : 0)} diff lines omitted)");
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

        if (focusThread is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"The following question was asked in a comment thread at: {FormatThreadLocation(focusThread)}");
            if (focusThread.FilePath is not null && focusFile is null)
            {
                sb.AppendLine("Referenced file context is unavailable in the supplied changed-file data.");
            }

            sb.AppendLine("Use the provided context and state any missing information.");
        }
        else
        {
            sb.AppendLine("Question thread context is unavailable.");
        }

        sb.AppendLine();
        sb.AppendLine($"Question: {question}");
        return sb.ToString();
    }

    private static (int Index, int HunkStart)? FindDiffLocation(string[] lines, int targetLine)
    {
        return FindDiffLocationOnSide(lines, targetLine, false)
               ?? FindDiffLocationOnSide(lines, targetLine, true);
    }

    private static (int Index, int HunkStart)? FindDiffLocationOnSide(string[] lines, int targetLine, bool removedOldSide)
    {
        var currentLine = 0;
        var hunkStart = -1;
        for (var index = 0; index < lines.Length; index++)
        {
            var header = HunkHeaderRegex.Match(lines[index]);
            if (header.Success)
            {
                if (!int.TryParse(
                        header.Groups[removedOldSide ? 1 : 2].Value, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out currentLine))
                {
                    hunkStart = -1;
                    continue;
                }

                hunkStart = index;
                continue;
            }

            if (hunkStart < 0 || lines[index].Length == 0)
            {
                continue;
            }

            var prefix = lines[index][0];
            if (prefix == ' ' || prefix == (removedOldSide ? '-' : '+'))
            {
                if (currentLine == targetLine && (!removedOldSide || prefix == '-'))
                {
                    return (index, hunkStart);
                }

                if (currentLine == int.MaxValue)
                {
                    hunkStart = -1;
                }
                else
                {
                    currentLine++;
                }
            }
        }

        return null;
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
        Message =
            "AgentMentionAnswerService: generating answer for PR#{PullRequestId}, question length {QuestionLength}")]
    private static partial void LogGeneratingAnswer(ILogger logger, int pullRequestId, int questionLength);
}
