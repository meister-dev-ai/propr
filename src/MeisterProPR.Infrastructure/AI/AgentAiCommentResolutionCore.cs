// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using System.Text.Json;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     AI implementation of <see cref="IAiCommentResolutionCore" /> that evaluates whether a
///     reviewer-authored comment thread has been resolved, using two distinct prompt paths:
///     (1) code-change evaluation and (2) conversational reply generation.
/// </summary>
public sealed class AgentAiCommentResolutionCore : IAiCommentResolutionCore
{
    private const string CodeChangeSystemPrompt = """
                                                  You are an expert code reviewer. A pull request has received new commits since you last
                                                  commented on a thread. Evaluate whether the latest code changes have addressed your original
                                                  concern. Be conservative: only mark as resolved if you are confident the issue is fixed.
                                                  If in doubt, return resolved=false.

                                                  Respond with valid JSON ONLY — no markdown fences, no preamble.
                                                  Schema: { "resolved": true|false, "replyText": "<optional short reply or null>" }
                                                  """;

    private const string ConversationalSystemPrompt = """
                                                      You are an expert code reviewer participating in a code review discussion. A developer has
                                                      replied to one of your comments. Read the thread history carefully and decide:

                                                      1. RESOLVED (resolved=true): The developer has acknowledged the issue, confirmed they won't
                                                         address it with a reasonable explanation, or explicitly asked to close the thread.
                                                         You MUST provide a replyText that clearly states WHY you are closing this thread
                                                         (e.g. "Closing — the added null-guard on line 12 directly addresses my concern." or
                                                         "Closing — the explanation about backward-compatibility is reasonable and I accept the
                                                         trade-off."). A closing comment without reasoning is not acceptable.

                                                      2. NOT RESOLVED (resolved=false): The issue is still open, the developer is asking a
                                                         question, or the reply needs a substantive response.
                                                         - Set replyText to a helpful response ONLY when you have something genuinely useful to
                                                           add (e.g. answering a direct question, clarifying your original concern, or pointing
                                                           to a specific fix).
                                                         - Set replyText to null when you are simply waiting for code changes and have nothing
                                                           new to contribute beyond what is already in the thread.

                                                      Be willing to close threads when the developer makes a reasonable case. Do not insist on
                                                      code changes if the developer explains why the current approach is acceptable.

                                                      Respond with valid JSON ONLY — no markdown fences, no preamble.
                                                      Schema: { "resolved": true|false, "replyText": "<required reasoning when resolved, helpful message or null when not resolved>" }
                                                      """;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <inheritdoc />
    public async Task<ThreadResolutionResult> EvaluateCodeChangeAsync(
        PrCommentThread thread,
        PullRequest pr,
        IChatClient chatClient,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        var userMessage = BuildCodeChangeUserMessage(thread, pr);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, CodeChangeSystemPrompt),
            new(ChatRole.User, userMessage),
        };

        var response = await chatClient.GetResponseAsync(messages, new ChatOptions { ModelId = modelId }, cancellationToken);
        return ParseResult(response.Text ?? "", response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount);
    }

    /// <inheritdoc />
    public async Task<ThreadResolutionResult> EvaluateConversationalReplyAsync(
        PrCommentThread thread,
        IChatClient chatClient,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        var userMessage = BuildConversationalUserMessage(thread);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, ConversationalSystemPrompt),
            new(ChatRole.User, userMessage),
        };

        var response = await chatClient.GetResponseAsync(messages, new ChatOptions { ModelId = modelId }, cancellationToken);
        return ParseResult(response.Text ?? "", response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount);
    }

    private static string BuildCodeChangeUserMessage(PrCommentThread thread, PullRequest pr)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Pull Request: {pr.Title}");
        sb.AppendLine($"{pr.SourceBranch} → {pr.TargetBranch}");
        sb.AppendLine();
        sb.AppendLine("## Thread to Evaluate");
        AppendThread(sb, thread);
        sb.AppendLine();

        if (thread.FilePath is not null)
        {
            // Only send the diff for the file this thread is anchored to.
            var relevantFile = pr.ChangedFiles.FirstOrDefault(f =>
                string.Equals(f.Path, thread.FilePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.OriginalPath, thread.FilePath, StringComparison.OrdinalIgnoreCase));

            if (relevantFile is not null)
            {
                sb.AppendLine("## Relevant File Change (latest iteration)");
                sb.AppendLine($"=== {relevantFile.Path} [{relevantFile.ChangeType}] ===");
                sb.AppendLine("--- DIFF ---");
                sb.AppendLine(relevantFile.UnifiedDiff);
            }
            else
            {
                sb.AppendLine($"The file `{thread.FilePath}` was not changed in the latest iteration.");
                sb.AppendLine("Other changed files: " + string.Join(", ", pr.ChangedFiles.Select(f => f.Path)));
            }
        }
        else
        {
            // PR-level thread: list changed files without their diffs to bound token usage.
            if (pr.ChangedFiles.Count > 0)
            {
                sb.AppendLine("## Changed Files in Latest Iteration");
                foreach (var file in pr.ChangedFiles)
                {
                    sb.AppendLine($"- {file.Path} [{file.ChangeType}]");
                }
            }
            else
            {
                sb.AppendLine("No file changes in this iteration.");
            }
        }

        return sb.ToString();
    }

    private static string BuildConversationalUserMessage(PrCommentThread thread)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Thread History");
        AppendThread(sb, thread);
        return sb.ToString();
    }

    private static void AppendThread(StringBuilder sb, PrCommentThread thread)
    {
        var location = thread.FilePath is not null
            ? $"{thread.FilePath}{(thread.LineNumber.HasValue ? $":L{thread.LineNumber}" : "")}"
            : "(PR-level)";

        sb.AppendLine($"Thread at {location}:");
        foreach (var comment in thread.Comments)
        {
            sb.AppendLine($"  [{comment.AuthorName}]: {comment.Content}");
        }
    }

    private static ThreadResolutionResult ParseResult(string json, long? inputTokens, long? outputTokens)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<ResolutionDto>(json, JsonOptions);
            if (dto is null)
            {
                return new ThreadResolutionResult(false, null, inputTokens, outputTokens);
            }

            return new ThreadResolutionResult(dto.Resolved, dto.ReplyText, inputTokens, outputTokens);
        }
        catch (JsonException)
        {
            return new ThreadResolutionResult(false, null, inputTokens, outputTokens);
        }
    }

    private sealed record ResolutionDto(bool Resolved, string? ReplyText);
}
