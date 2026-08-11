// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using System.Text.Json;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Infrastructure.AI;

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

                                                  When you return resolved=true, you MUST also provide a non-empty replyText that explains
                                                  why the latest change addresses the concern. Do not close the thread silently.
                                                  When you return resolved=false, set replyText to null unless a short clarification is truly
                                                  necessary.

                                                  Respond with valid JSON ONLY — no markdown fences, no preamble.
                                                  Schema: { "resolved": true|false, "replyText": "<required reasoning when resolved, otherwise null>" }
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

    /// <summary>
    ///     How many files one evaluation may retrieve. The governing limit is the model's context window,
    ///     which determines how much of a diff fits; this limit only prevents a request for a long list of
    ///     small files from producing the same number of provider calls, because a file's size is unknown
    ///     until it has been retrieved. It is set low because retrieving one file's diff takes several
    ///     provider round trips.
    /// </summary>
    private const int MaxRequestedFiles = 5;

    /// <summary>
    ///     How many changed paths one thread's prompt lists. The list allows an evaluation to distinguish a
    ///     fix that was never made from one it was not supplied with. A few dozen paths convey that as well
    ///     as thousands would, at a fraction of the context window on a large pull request.
    /// </summary>
    private const int MaxListedChangedFiles = 40;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <inheritdoc />
    public async Task<ThreadResolutionResult> EvaluateCodeChangeAsync(
        PrCommentThread thread,
        PullRequest pr,
        IChatClient chatClient,
        string modelId,
        CancellationToken cancellationToken = default,
        string? outputLanguage = null,
        bool hasNewReplies = false,
        ThreadEvidenceAccess? evidence = null)
    {
        // The instructions for requesting another file apply only when the manifest lists one that has not
        // already been supplied. Otherwise they describe a list that is absent.
        var canAskForFiles = evidence is not null && HasUnshownChangedFiles(thread, pr);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, BuildCodeChangeSystemPrompt(hasNewReplies, outputLanguage, canAskForFiles, false)),
            new(ChatRole.User, BuildCodeChangeUserMessage(thread, pr, [])),
        };

        var response = await chatClient.GetResponseAsync(
            messages,
            new ChatOptions { ModelId = modelId },
            cancellationToken);
        var calls = new List<ThreadResolutionCall> { ToCall(AiTokenUsageExtractor.FromResponse(response)) };
        var dto = ParseDto(response.Text ?? "");

        // A verdict that already resolved the thread is complete. Requesting more would spend a second call
        // re-evaluating a settled finding, and could produce a result contradicting the first.
        var requested = !canAskForFiles || dto is null || dto.Resolved
            ? []
            : ResolveRequestedPaths(ReadRequestedPaths(dto.NeedFiles), thread, pr, evidence!.OnRequestRejected);
        if (requested.Count == 0)
        {
            return BuildResult(dto, calls, true);
        }

        // Retrieved after the verdict rather than before it, because most threads issue no request. The
        // extra round is the cost of a cross-file finding; a same-file one still costs one call and one diff.
        var fetched = await FetchWithinBudgetAsync(requested, evidence!, messages, cancellationToken);
        if (fetched.Count == 0)
        {
            // Nothing was returned, so a second call would evaluate the same evidence twice and bill for it
            // twice.
            return BuildResult(dto, calls, true);
        }

        var finalMessages = new List<ChatMessage>
        {
            new(ChatRole.System, BuildCodeChangeSystemPrompt(hasNewReplies, outputLanguage, true, true)),
            new(ChatRole.User, BuildCodeChangeUserMessage(thread, pr, fetched)),
        };

        ChatResponse finalResponse;
        try
        {
            finalResponse = await chatClient.GetResponseAsync(
                finalMessages,
                new ChatOptions { ModelId = modelId },
                cancellationToken);
        }
        catch (BudgetHardCapReachedException)
        {
            // The cap is checked before a call, so no further spend occurred. Returning the first round's
            // result allows its spend to be recorded rather than discarded, and the cap still terminates the
            // pass at the next call it refuses.
            return BuildResult(dto, calls, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The first call was made and is already billable. Propagating this exception would discard both
            // the result it produced and the record of its spend, which is a worse outcome than answering the
            // thread with less evidence.
            return BuildResult(dto, calls, true);
        }

        calls.Add(ToCall(AiTokenUsageExtractor.FromResponse(finalResponse)));

        // An unparseable second result falls back to the first, which is a valid verdict on valid evidence.
        // Both calls are still counted, because both were spent.
        return BuildResult(ParseDto(finalResponse.Text ?? "") ?? dto, calls, true);
    }

    /// <summary>
    ///     Reduces the requested paths to those that are permitted: paths this pull request changed, each
    ///     one once, excluding any file whose diff was already supplied.
    /// </summary>
    /// <remarks>
    ///     A path is matched exactly first and only then without regard to case, because Azure DevOps anchors
    ///     threads to repo-root-absolute paths while the changed-file manifest holds repo-relative ones, and a
    ///     request in either form refers to the same file. Preferring the exact match prevents two paths that
    ///     differ only in case, which a case-sensitive repository permits, from resolving to each other. The
    ///     path passed to the fetcher is always the manifest's own, so no model-generated string reaches the
    ///     provider, and any path absent from the manifest is discarded rather than retrieved. That is the
    ///     control against a comment crafted to direct the reviewer at other code.
    /// </remarks>
    private static IReadOnlyList<string> ResolveRequestedPaths(
        IReadOnlyList<string> needFiles,
        PrCommentThread thread,
        PullRequest pr,
        Action<string>? onRequestRejected)
    {
        if (needFiles.Count == 0)
        {
            return [];
        }

        var exactManifest = new Dictionary<string, string>(StringComparer.Ordinal);
        var looseManifest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var summary in pr.AllPrFileSummaries)
        {
            var key = NormalizePathForMatch(summary.Path);
            exactManifest.TryAdd(key, summary.Path);
            looseManifest.TryAdd(key, summary.Path);
        }

        // Excluded only when its diff was actually supplied. A thread anchored to a file the current
        // revision did not change, or whose diff failed to load, received no content for it, so a request
        // for it is legitimate. A renamed file is excluded under both of its names, because a thread
        // anchored before the rename holds the old one while the manifest holds the new.
        var anchorFile = FindAnchorFile(thread, pr);
        var anchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (anchorFile is not null)
        {
            anchors.Add(NormalizePathForMatch(thread.FilePath!));
            anchors.Add(NormalizePathForMatch(anchorFile.Path));
            if (anchorFile.OriginalPath is not null)
            {
                anchors.Add(NormalizePathForMatch(anchorFile.OriginalPath));
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<string>();

        foreach (var requested in needFiles)
        {
            if (string.IsNullOrWhiteSpace(requested))
            {
                continue;
            }

            var key = NormalizePathForMatch(requested);
            if (key.Length == 0 || anchors.Contains(key) || !seen.Add(key))
            {
                continue;
            }

            if (!exactManifest.TryGetValue(key, out var manifestPath)
                && !looseManifest.TryGetValue(key, out manifestPath))
            {
                onRequestRejected?.Invoke(requested);
                continue;
            }

            resolved.Add(manifestPath);
            if (resolved.Count == MaxRequestedFiles)
            {
                break;
            }
        }

        return resolved;
    }

    /// <summary>Whether the manifest lists a changed file whose diff has not already been supplied.</summary>
    private static bool HasUnshownChangedFiles(PrCommentThread thread, PullRequest pr)
    {
        var anchor = thread.FilePath is null ? null : NormalizePathForMatch(thread.FilePath);

        return pr.AllPrFileSummaries.Any(summary =>
            !string.Equals(NormalizePathForMatch(summary.Path), anchor, StringComparison.OrdinalIgnoreCase));
    }

    private static ChangedFile? FindAnchorFile(PrCommentThread thread, PullRequest pr)
    {
        return thread.FilePath is null
            ? null
            : pr.ChangedFiles.FirstOrDefault(file =>
                string.Equals(file.Path, thread.FilePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(file.OriginalPath, thread.FilePath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Retrieves the requested diffs while they fit the model's context window, stopping at the first one
    ///     that does not, so a large diff cannot cause a sequence of retrievals that cannot be used.
    /// </summary>
    private static async Task<IReadOnlyList<ChangedFile>> FetchWithinBudgetAsync(
        IReadOnlyList<string> paths,
        ThreadEvidenceAccess evidence,
        IReadOnlyList<ChatMessage> sentSoFar,
        CancellationToken cancellationToken)
    {
        var budget = ReviewContextBudget.ComputeInputBudget(
            ReviewContextBudget.ResolveMaxContextTokens(evidence.MaxContextTokens),
            0);
        var remaining = budget - ReviewContextBudget.EstimateMessagesTokens(evidence.TokenizerName, sentSoFar);
        var fetched = new List<ChangedFile>();

        foreach (var path in paths)
        {
            if (remaining <= 0)
            {
                break;
            }

            ChangedFile? file;
            try
            {
                file = await evidence.FetchFileDiffAsync(path, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A file that cannot be read is omitted from the evaluation. The caller performs the
                // logging, and leaving the thread unanswered over it is a worse outcome than answering it
                // with less evidence.
                continue;
            }

            // A binary file, or one whose diff is empty, contains nothing to evaluate. Counting it as
            // evidence would spend a second call presenting a heading with no code beneath it, which the
            // final-round wording describes as the file not having fitted.
            if (file is null || file.IsBinary || string.IsNullOrWhiteSpace(file.UnifiedDiff))
            {
                continue;
            }

            var cost = ReviewContextBudget.EstimateTokens(evidence.TokenizerName, file.UnifiedDiff);
            if (cost > remaining)
            {
                break;
            }

            remaining -= cost;
            fetched.Add(file);
        }

        return fetched;
    }

    private static ThreadResolutionCall ToCall(AiTokenUsage usage)
    {
        return usage.IsEstimated
            ? new ThreadResolutionCall()
            : new ThreadResolutionCall(
                usage.InputTokens,
                usage.OutputTokens,
                usage.CachedInputTokens,
                usage.CacheWriteTokens,
                usage.ReasoningTokens);
    }

    /// <inheritdoc />
    public async Task<ThreadResolutionResult> EvaluateConversationalReplyAsync(
        PrCommentThread thread,
        IChatClient chatClient,
        string modelId,
        CancellationToken cancellationToken = default,
        string? outputLanguage = null)
    {
        var userMessage = BuildConversationalUserMessage(thread);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, OutputLanguageDirective.Append(ConversationalSystemPrompt, outputLanguage)),
            new(ChatRole.User, userMessage),
        };

        var response = await chatClient.GetResponseAsync(
            messages,
            new ChatOptions { ModelId = modelId },
            cancellationToken);
        var usage = AiTokenUsageExtractor.FromResponse(response);
        return ParseResult(response.Text ?? "", usage);
    }

    /// <summary>
    ///     Assembles the system prompt for one code-change call: the base rules, the reply rule when a person
    ///     is waiting for an answer, and the rules for requesting code in another file when the caller permits
    ///     it.
    /// </summary>
    private static string BuildCodeChangeSystemPrompt(
        bool hasNewReplies,
        string? outputLanguage,
        bool canAskForFiles,
        bool finalRound)
    {
        var prompt = AppendDeveloperReplyDirective(CodeChangeSystemPrompt, hasNewReplies);

        if (canAskForFiles)
        {
            prompt = string.Concat(
                prompt,
                Environment.NewLine,
                Environment.NewLine,
                PromptTemplateRuntime.RenderCrossFileEvidence(finalRound));
        }

        return OutputLanguageDirective.Append(prompt, outputLanguage);
    }

    /// <summary>
    ///     Adds the rule that governs a thread carrying both a code change and an unanswered reply. The wording
    ///     lives in the prompt template tree rather than in this file, so every prompt fragment stays editable
    ///     in one place.
    /// </summary>
    private static string AppendDeveloperReplyDirective(string prompt, bool hasNewReplies)
    {
        if (!hasNewReplies)
        {
            return prompt;
        }

        return string.Concat(
            prompt,
            Environment.NewLine,
            Environment.NewLine,
            PromptTemplateRuntime.RenderDeveloperReply());
    }

    private static string BuildCodeChangeUserMessage(
        PrCommentThread thread,
        PullRequest pr,
        IReadOnlyList<ChangedFile> requestedFiles)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Pull Request: {pr.Title}");
        sb.AppendLine($"{pr.SourceBranch} → {pr.TargetBranch}");
        sb.AppendLine();
        sb.AppendLine("## Thread to Evaluate");
        AppendThread(sb, thread);
        sb.AppendLine();

        // Every changed path, by name only. The file a fix changes is often not the file the comment is
        // anchored to, and an evaluation supplied with one diff and no list cannot distinguish a fix that is
        // absent from one it was not supplied with.
        AppendChangedFileManifest(sb, pr, thread);

        if (thread.FilePath is not null)
        {
            var relevantFile = FindAnchorFile(thread, pr);

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
            }
        }

        if (requestedFiles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Further File Changes You Asked For");
            foreach (var file in requestedFiles)
            {
                sb.AppendLine($"=== {file.Path} [{file.ChangeType}] ===");
                sb.AppendLine("--- DIFF ---");
                sb.AppendLine(file.UnifiedDiff);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Lists the changed paths, bounded so that a pull request changing thousands of files does not turn
    ///     every thread's prompt into a file listing. The omitted count is stated rather than left implicit,
    ///     because an evaluation treating the list as complete would exclude a file that is on it.
    /// </summary>
    private static void AppendChangedFileManifest(StringBuilder sb, PullRequest pr, PrCommentThread thread)
    {
        var summaries = pr.AllPrFileSummaries;
        if (summaries.Count == 0)
        {
            sb.AppendLine("No file changes in this iteration.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("## Files Changed in This Pull Request");
        foreach (var summary in OrderByNearnessToAnchor(summaries, thread).Take(MaxListedChangedFiles))
        {
            sb.AppendLine($"- {summary.Path} [{summary.ChangeType}]");
        }

        if (summaries.Count > MaxListedChangedFiles)
        {
            sb.AppendLine(
                $"...and {summaries.Count - MaxListedChangedFiles} further changed files, not listed here. "
                + "Do not treat this list as the whole pull request. You may still ask for a path you know "
                + "the name of, whether or not it appears above.");
        }

        sb.AppendLine();
    }

    /// <summary>
    ///     Orders the files nearest the thread first, so the ones the listing has room for are those most
    ///     likely to contain a fix for this finding. A change addressing a comment is usually near the code
    ///     the comment refers to.
    /// </summary>
    private static IEnumerable<ChangedFileSummary> OrderByNearnessToAnchor(
        IReadOnlyList<ChangedFileSummary> summaries,
        PrCommentThread thread)
    {
        if (thread.FilePath is null)
        {
            return summaries;
        }

        var anchorDirectory = DirectoryOf(NormalizePathForMatch(thread.FilePath));

        return anchorDirectory.Length == 0
            ? summaries
            : summaries.OrderByDescending(summary =>
                string.Equals(
                    DirectoryOf(NormalizePathForMatch(summary.Path)),
                    anchorDirectory,
                    StringComparison.OrdinalIgnoreCase));
    }

    private static string DirectoryOf(string path)
    {
        var lastSeparator = path.LastIndexOf('/');
        return lastSeparator < 0 ? string.Empty : path[..lastSeparator];
    }

    private static string NormalizePathForMatch(string path)
    {
        return path.Trim().Replace('\\', '/').TrimStart('/');
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
        var location = FormatThreadLocation(thread);

        sb.AppendLine($"Thread at {location}:");
        foreach (var comment in thread.Comments)
        {
            sb.AppendLine($"  [{comment.AuthorName}]: {comment.Content}");
        }
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

    private static ThreadResolutionResult ParseResult(
        string json,
        AiTokenUsage usage,
        bool requireReplyWhenResolved = false)
    {
        return BuildResult(ParseDto(json), [ToCall(usage)], requireReplyWhenResolved);
    }

    private static ResolutionDto? ParseDto(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ResolutionDto>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Combines the model's result and the cost of each call of this evaluation into one value. The token
    ///     fields hold the total across every call, because that total is what the client is billed; the
    ///     per-call figures are retained alongside so a trace can distinguish them.
    /// </summary>
    private static ThreadResolutionResult BuildResult(
        ResolutionDto? dto,
        IReadOnlyList<ThreadResolutionCall> calls,
        bool requireReplyWhenResolved)
    {
        var input = SumTokens(calls, call => call.InputTokens);
        var output = SumTokens(calls, call => call.OutputTokens);
        var cached = SumTokens(calls, call => call.CachedInputTokens);
        var cacheWrite = SumTokens(calls, call => call.CacheWriteTokens);
        var reasoning = SumTokens(calls, call => call.ReasoningTokens);
        var spentCalls = calls.Count > 1 ? calls : null;

        var normalizedReplyText = NormalizeReplyText(dto?.ReplyText);
        var resolved = dto?.Resolved == true
                       && (!requireReplyWhenResolved || normalizedReplyText is not null);

        return new ThreadResolutionResult(
            resolved,
            resolved || dto?.Resolved != true ? normalizedReplyText : null,
            input,
            output,
            cached,
            cacheWrite,
            reasoning,
            spentCalls);
    }

    /// <summary>Totals one token field, remaining <see langword="null" /> when no call reported it.</summary>
    private static long? SumTokens(
        IReadOnlyList<ThreadResolutionCall> calls,
        Func<ThreadResolutionCall, long?> select)
    {
        long total = 0;
        var reported = false;

        foreach (var call in calls)
        {
            if (select(call) is { } value)
            {
                total += value;
                reported = true;
            }
        }

        return reported ? total : null;
    }

    private static string? NormalizeReplyText(string? replyText)
    {
        return string.IsNullOrWhiteSpace(replyText) ? null : replyText.Trim();
    }

    /// <param name="NeedFiles">
    ///     Paths whose diffs the evaluation requires before evaluating, held as raw JSON. Honoured only for
    ///     files this pull request changed, and only on the first call of an evaluation. Typed as an element
    ///     rather than a list of strings so that a model emitting this field in the wrong shape, a bare string
    ///     or a list of objects, does not invalidate the verdict alongside it. The request can be discarded;
    ///     the result cannot.
    /// </param>
    private sealed record ResolutionDto(bool Resolved, string? ReplyText, JsonElement? NeedFiles = null);

    /// <summary>Extracts the requested paths from whichever shape the model emitted.</summary>
    private static IReadOnlyList<string> ReadRequestedPaths(JsonElement? needFiles)
    {
        if (needFiles is not { } element)
        {
            return [];
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() is { } single ? [single] : [];
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var paths = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } path)
            {
                paths.Add(path);
            }
        }

        return paths;
    }
}
