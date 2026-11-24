// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Deduplication;

/// <summary>
///     Groups the findings of a pull request by root cause with one model call. It resolves the model bound to
///     <see cref="AiPurpose.ReviewVerification" />, as the merge judge does, and sends a table of all findings: id, path,
///     line, classification and the message cut to 1,200 characters. The answer is checked deterministically. An unknown
///     id, an id in two groups, a group with more than half of the findings of a call with more than six findings, a
///     malformed answer and a failed call each lead to no groups, so a wrong answer cannot merge findings. A pull request
///     with more than <see cref="MaxFindingsPerCall" /> findings is grouped in two rounds: first within chunks of files
///     that share a directory, then over one representative per group and every finding without a group.
/// </summary>
public sealed class AiFindingRootCauseGrouper(IAiRuntimeResolver? aiRuntimeResolver = null) : IFindingRootCauseGrouper
{
    /// <summary>The most findings one call groups. A pull request with more findings is grouped in two rounds.</summary>
    public const int MaxFindingsPerCall = 80;

    private const int MaxMessageChars = 1200;

    private const int MaxCauseChars = 200;

    // Up to this many findings, a group may hold every finding of a call. Above it, a group with more than half of the
    // findings is taken as a misreading of the task and rejects the answer.
    private const int OversizedGroupCheckMinimum = 6;

    private static readonly JsonSerializerOptions FindingLineJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <inheritdoc />
    public async Task<FindingRootCauseGrouping> GroupAsync(
        IReadOnlyList<ReviewComment> comments,
        Guid clientId,
        ReviewSystemContext? context = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(comments);
        if (comments.Count < 2)
        {
            return FindingRootCauseGrouping.None;
        }

        if (aiRuntimeResolver is null || clientId == Guid.Empty)
        {
            return new FindingRootCauseGrouping([], [], FindingRootCauseGrouping.ModelUnavailableReason);
        }

        IResolvedAiChatRuntime runtime;
        try
        {
            runtime = await aiRuntimeResolver.ResolveChatRuntimeAsync(clientId, AiPurpose.ReviewVerification, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new FindingRootCauseGrouping([], [], FindingRootCauseGrouping.ModelUnavailableReason);
        }

        string systemPrompt;
        try
        {
            systemPrompt = ReviewPrompts.BuildFindingRootCauseGroupingSystemPrompt(context);
        }
        catch (InvalidOperationException)
        {
            // A template that cannot be rendered leaves the findings apart; the review continues without the grouping.
            return new FindingRootCauseGrouping([], [], FindingRootCauseGrouping.CallFailedReason);
        }

        await RecordPromptEvidenceAsync(context, systemPrompt, ct).ConfigureAwait(false);

        var spend = new List<ModelCallSpend>();
        var all = Enumerable.Range(0, comments.Count).ToList();
        if (comments.Count <= MaxFindingsPerCall)
        {
            var answer = await AskAsync(runtime, systemPrompt, comments, all, spend, ct).ConfigureAwait(false);
            return new FindingRootCauseGrouping(answer.Groups, spend, answer.FallbackReason);
        }

        return await GroupInTwoRoundsAsync(runtime, systemPrompt, comments, all, spend, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Splits findings into chunks of at most <see cref="MaxFindingsPerCall" /> findings. Findings of one directory stay
    ///     in one chunk unless the directory alone has more findings than a chunk holds, and neighbouring directories share a
    ///     chunk while it has room.
    /// </summary>
    /// <param name="comments">The findings of the pull request.</param>
    /// <param name="indexes">The positions of the findings to split.</param>
    internal static IReadOnlyList<IReadOnlyList<int>> BuildChunks(IReadOnlyList<ReviewComment> comments, IReadOnlyList<int> indexes)
    {
        var directories = indexes
            .GroupBy(index => DirectoryOf(comments[index].FilePath), StringComparer.Ordinal)
            .OrderBy(directory => directory.Key, StringComparer.Ordinal)
            .Select(directory => directory
                .OrderBy(index => comments[index].FilePath, StringComparer.Ordinal)
                .ThenBy(index => comments[index].LineNumber ?? 0)
                .ThenBy(index => index)
                .ToList());

        var chunks = new List<IReadOnlyList<int>>();
        var current = new List<int>();
        foreach (var directory in directories)
        {
            if (directory.Count > MaxFindingsPerCall)
            {
                Flush();
                chunks.AddRange(directory.Chunk(MaxFindingsPerCall));
                continue;
            }

            if (current.Count + directory.Count > MaxFindingsPerCall)
            {
                Flush();
            }

            current.AddRange(directory);
        }

        Flush();
        return chunks;

        void Flush()
        {
            if (current.Count > 0)
            {
                chunks.Add(current);
                current = [];
            }
        }
    }

    // The first round groups within chunks of neighbouring directories. The second round asks over one representative of
    // each first-round group, its primary, and over every finding without a group, so a cause that spans directories can
    // still join. A first round without any group would repeat its own chunks in the second round, so the second round
    // runs only after the first found a group.
    private static async Task<FindingRootCauseGrouping> GroupInTwoRoundsAsync(
        IResolvedAiChatRuntime runtime,
        string systemPrompt,
        IReadOnlyList<ReviewComment> comments,
        IReadOnlyList<int> all,
        List<ModelCallSpend> spend,
        CancellationToken ct)
    {
        string? fallbackReason = null;
        var firstRound = new List<FindingRootCauseGroup>();
        foreach (var chunk in BuildChunks(comments, all).Where(chunk => chunk.Count >= 2))
        {
            var answer = await AskAsync(runtime, systemPrompt, comments, chunk, spend, ct).ConfigureAwait(false);
            fallbackReason ??= answer.FallbackReason;
            firstRound.AddRange(answer.Groups);
        }

        if (firstRound.Count == 0)
        {
            return new FindingRootCauseGrouping([], spend, fallbackReason);
        }

        var groupOfRepresentative = new Dictionary<int, FindingRootCauseGroup>();
        foreach (var group in firstRound)
        {
            groupOfRepresentative[group.PrimaryIndex ?? group.MemberIndexes[0]] = group;
        }

        var grouped = firstRound.SelectMany(group => group.MemberIndexes).ToHashSet();
        var representatives = groupOfRepresentative.Keys.Concat(all.Where(index => !grouped.Contains(index))).Order().ToList();

        var joined = new List<FindingRootCauseGroup>();
        var absorbed = new HashSet<FindingRootCauseGroup>(ReferenceEqualityComparer.Instance);
        foreach (var chunk in BuildChunks(comments, representatives).Where(chunk => chunk.Count >= 2))
        {
            var answer = await AskAsync(runtime, systemPrompt, comments, chunk, spend, ct).ConfigureAwait(false);
            fallbackReason ??= answer.FallbackReason;
            var expanded = answer.Groups
                .Select(group => new FindingRootCauseGroup(
                    group.MemberIndexes
                        .SelectMany(representative => groupOfRepresentative.TryGetValue(representative, out var part) ? part.MemberIndexes : [representative])
                        .Distinct()
                        .Order()
                        .ToList(),
                    group.PrimaryIndex,
                    group.Cause))
                .ToList();
            if (expanded.Any(group => IsOversized(group.MemberIndexes.Count, comments.Count)))
            {
                fallbackReason ??= FindingRootCauseGrouping.OversizedGroupReason;
                continue;
            }

            joined.AddRange(expanded);
            foreach (var representative in answer.Groups.SelectMany(group => group.MemberIndexes))
            {
                if (groupOfRepresentative.TryGetValue(representative, out var part))
                {
                    absorbed.Add(part);
                }
            }
        }

        var groups = firstRound.Where(group => !absorbed.Contains(group)).Concat(joined).ToList();
        return new FindingRootCauseGrouping(groups, spend, fallbackReason);
    }

    private static async Task<CallAnswer> AskAsync(
        IResolvedAiChatRuntime runtime,
        string systemPrompt,
        IReadOnlyList<ReviewComment> comments,
        IReadOnlyList<int> indexes,
        List<ModelCallSpend> spend,
        CancellationToken ct)
    {
        ChatResponse response;
        try
        {
            response = await runtime.ChatClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, systemPrompt),
                    new ChatMessage(ChatRole.User, BuildUserMessage(comments, indexes)),
                ],
                new ChatOptions { ModelId = runtime.Model.RemoteModelId },
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return CallAnswer.Fallback(FindingRootCauseGrouping.CallFailedReason);
        }

        if (MeasureSpend(runtime, response) is { } callSpend)
        {
            spend.Add(callSpend);
        }

        return ParseAnswer(response.Text, indexes);
    }

    internal static string FindingId(int index) => string.Create(CultureInfo.InvariantCulture, $"F{index + 1}");

    private static string BuildUserMessage(IReadOnlyList<ReviewComment> comments, IReadOnlyList<int> indexes)
    {
        var builder = new StringBuilder("Findings of the pull request, one JSON object per line:\n");
        foreach (var index in indexes)
        {
            var comment = comments[index];
            var classification = comment.FindingClassification;
            var line = new
            {
                id = FindingId(index),
                path = comment.FilePath,
                line = comment.LineNumber,
                severity = comment.Severity.ToString().ToLowerInvariant(),
                types = classification?.CoreSlugs is { Count: > 0 } slugs ? slugs : null,
                qualifier = classification?.Qualifier?.ToString().ToLowerInvariant(),
                message = comment.Message.Length <= MaxMessageChars ? comment.Message : comment.Message[..MaxMessageChars],
            };
            builder.AppendLine(JsonSerializer.Serialize(line, FindingLineJsonOptions));
        }

        return builder.ToString();
    }

    // Reads {"groups":[{"finding_ids":[...],"primary_finding_id":"...","cause":"..."}]}. A group with fewer than two
    // distinct ids joins nothing and is skipped. An id repeated within one group counts once. A primary that is not a
    // member of its group is dropped, and the consolidation chooses the primary itself.
    private static CallAnswer ParseAnswer(string? text, IReadOnlyList<int> indexes)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return CallAnswer.Fallback(FindingRootCauseGrouping.MalformedAnswerReason);
        }

        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return CallAnswer.Fallback(FindingRootCauseGrouping.MalformedAnswerReason);
        }

        var known = indexes.ToDictionary(FindingId, index => index, StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(text[start..(end + 1)]);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("groups", out var groupsElement)
                || groupsElement.ValueKind != JsonValueKind.Array)
            {
                return CallAnswer.Fallback(FindingRootCauseGrouping.MalformedAnswerReason);
            }

            var assigned = new HashSet<int>();
            var groups = new List<FindingRootCauseGroup>();
            foreach (var item in groupsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("finding_ids", out var idsElement)
                    || idsElement.ValueKind != JsonValueKind.Array)
                {
                    return CallAnswer.Fallback(FindingRootCauseGrouping.MalformedAnswerReason);
                }

                var members = new List<int>();
                foreach (var idElement in idsElement.EnumerateArray())
                {
                    if (idElement.ValueKind != JsonValueKind.String)
                    {
                        return CallAnswer.Fallback(FindingRootCauseGrouping.MalformedAnswerReason);
                    }

                    if (!known.TryGetValue(idElement.GetString()!.Trim(), out var index))
                    {
                        return CallAnswer.Fallback(FindingRootCauseGrouping.UnknownFindingIdReason);
                    }

                    if (members.Contains(index))
                    {
                        continue;
                    }

                    if (!assigned.Add(index))
                    {
                        return CallAnswer.Fallback(FindingRootCauseGrouping.FindingInTwoGroupsReason);
                    }

                    members.Add(index);
                }

                if (members.Count < 2)
                {
                    continue;
                }

                if (IsOversized(members.Count, indexes.Count))
                {
                    return CallAnswer.Fallback(FindingRootCauseGrouping.OversizedGroupReason);
                }

                int? primary = item.TryGetProperty("primary_finding_id", out var primaryElement)
                               && primaryElement.ValueKind == JsonValueKind.String
                               && known.TryGetValue(primaryElement.GetString()!.Trim(), out var primaryIndex)
                               && members.Contains(primaryIndex)
                    ? primaryIndex
                    : null;
                var cause = item.TryGetProperty("cause", out var causeElement) && causeElement.ValueKind == JsonValueKind.String
                    ? BoundCause(causeElement.GetString())
                    : null;
                groups.Add(new FindingRootCauseGroup(members.Order().ToList(), primary, cause));
            }

            return new CallAnswer(groups, null);
        }
        catch (JsonException)
        {
            return CallAnswer.Fallback(FindingRootCauseGrouping.MalformedAnswerReason);
        }
    }

    private static bool IsOversized(int memberCount, int findingCount) =>
        findingCount > OversizedGroupCheckMinimum && memberCount * 2 > findingCount;

    private static string? BoundCause(string? cause)
    {
        if (string.IsNullOrWhiteSpace(cause))
        {
            return null;
        }

        var trimmed = cause.Trim();
        return trimmed.Length <= MaxCauseChars ? trimmed : trimmed[..MaxCauseChars];
    }

    private static string DirectoryOf(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return string.Empty;
        }

        var normalized = filePath.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalized[..slash];
    }

    // The prompt evidence of an experiment is diagnostics. A failure to record it leaves the grouping unchanged.
    private static async Task RecordPromptEvidenceAsync(ReviewSystemContext? context, string systemPrompt, CancellationToken ct)
    {
        if (context is null)
        {
            return;
        }

        try
        {
            await PromptStageEvidenceRecorder.RecordAsync(context, PromptStageKeys.FindingRootCauseGroupingSystem, systemPrompt, null, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The evidence is lost; the grouping proceeds.
        }
    }

    // A response without a usage payload extracts as all-zero. It is not reported, because a breakdown line with zero
    // tokens would state that the call was free.
    private static ModelCallSpend? MeasureSpend(IResolvedAiChatRuntime runtime, ChatResponse response)
    {
        var usage = AiTokenUsageExtractor.FromResponse(response);
        if (usage.InputTokens <= 0 && usage.OutputTokens <= 0)
        {
            return null;
        }

        return new ModelCallSpend(
            runtime.Model.RemoteModelId,
            runtime.LogicalModelName,
            usage.InputTokens,
            usage.OutputTokens,
            usage.CachedInputTokens,
            usage.CacheWriteTokens,
            usage.ReasoningTokens);
    }

    private sealed record CallAnswer(IReadOnlyList<FindingRootCauseGroup> Groups, string? FallbackReason)
    {
        public static CallAnswer Fallback(string reason) => new([], reason);
    }
}
