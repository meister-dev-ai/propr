// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Deduplication;

/// <summary>
///     AI-judged same-defect-class decision for the semantic deduplicator. It resolves an independent judging
///     model bound to <see cref="AiPurpose.ReviewVerification" /> — the same binding idiom the evidence-backed
///     verifier uses — and asks a skeptical bounded call whether two findings describe the same defect. It is
///     degraded-safe and conservative: any missing binding, parse failure, or exception returns
///     <see langword="false" />, so the deduplicator keeps both findings rather than risk merging distinct bugs.
///     An embeddings-based judge is an alternative implementation of the same interface.
/// </summary>
public sealed class AiFindingMergeJudge(IAiRuntimeResolver? aiRuntimeResolver = null) : IFindingMergeJudge
{
    private const int MaxMessageChars = 1200;

    /// <inheritdoc />
    public async Task<bool> AreSameDefectClassAsync(
        CandidateReviewFinding first,
        CandidateReviewFinding second,
        Guid clientId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        if (aiRuntimeResolver is null || clientId == Guid.Empty)
        {
            return false;
        }

        try
        {
            var runtime = await aiRuntimeResolver
                .ResolveChatRuntimeAsync(clientId, AiPurpose.ReviewVerification, ct)
                .ConfigureAwait(false);

            var response = await runtime.ChatClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, BuildSystemPrompt()),
                    new ChatMessage(ChatRole.User, BuildUserMessage(first, second)),
                ],
                new ChatOptions { ModelId = runtime.Model.RemoteModelId },
                ct).ConfigureAwait(false);

            return ParseSameDefect(response.Text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Degraded-safe: when the judgment cannot be made, keep both findings.
            return false;
        }
    }

    private static string BuildSystemPrompt()
    {
        return "You are a strict code-review de-duplication judge. You are given two review findings that both "
               + "point at the same file and effectively the same lines. Decide whether they describe the SAME "
               + "underlying defect (one issue reported twice) or TWO DISTINCT defects that merely share vocabulary "
               + "or location. Answer SAME only when a single fix would resolve both. If they are different defects, "
               + "or you are unsure, answer DIFFERENT. Respond with ONLY a JSON object and nothing else: "
               + "{\"verdict\":\"same|different\",\"reason\":\"<one short sentence>\"}.";
    }

    private static string BuildUserMessage(CandidateReviewFinding first, CandidateReviewFinding second)
    {
        return $"FINDING A ({Describe(first)}):\n{Truncate(first.Message)}\n\n"
               + $"FINDING B ({Describe(second)}):\n{Truncate(second.Message)}";
    }

    private static string Describe(CandidateReviewFinding finding)
    {
        var line = finding.LineNumber?.ToString() ?? "(unknown)";
        return $"{finding.FilePath}:{line} severity={finding.Severity}";
    }

    private static string Truncate(string value)
    {
        return value.Length <= MaxMessageChars ? value : value[..MaxMessageChars];
    }

    private static bool ParseSameDefect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("verdict", out var verdictEl)
                || verdictEl.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            return string.Equals(verdictEl.GetString()?.Trim(), "same", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
