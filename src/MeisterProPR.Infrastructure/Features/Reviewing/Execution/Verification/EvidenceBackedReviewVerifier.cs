// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;

/// <summary>
///     Evidence-gathering verifier for claims the deterministic verifier can only withhold for lack of
///     bounded evidence. For each work item it reads the anchor file via the review-context tools and asks a
///     skeptical bounded judging call whether the asserted defect is actually present in the code. It can only
///     PROMOTE a claim to publication when the evidence confirms it; on refusal, missing context, or any
///     failure it returns the same conservative withhold the deterministic verifier produces — so it never
///     reduces precision below the current behavior, it only recovers real findings the gate was discarding.
/// </summary>
public sealed class EvidenceBackedReviewVerifier : IReviewFindingVerifier
{
    private const int MaxAnchorChars = 8000;
    private const int MaxAnchorLines = 400;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<VerificationOutcome>> VerifyAsync(
        IReadOnlyList<VerificationWorkItem> workItems,
        IReadOnlyList<InvariantFact> invariantFacts,
        ReviewVerificationContext? verificationContext = null,
        CancellationToken ct = default)
    {
        _ = invariantFacts;
        ArgumentNullException.ThrowIfNull(workItems);

        // Resolve the judging runtime once for the whole work-item set: prefer an independent model bound to
        // AiPurpose.ReviewVerification, falling back to the reviewer's own tier client when no resolver/binding
        // is available.
        var (judgeClient, judgeModel) = await ResolveJudgeRuntimeAsync(verificationContext, ct).ConfigureAwait(false);

        var outcomes = new List<VerificationOutcome>(workItems.Count);
        foreach (var workItem in workItems)
        {
            ct.ThrowIfCancellationRequested();
            outcomes.Add(await this.VerifyOneAsync(workItem, verificationContext, judgeClient, judgeModel, ct).ConfigureAwait(false));
        }

        return outcomes;
    }

    private static async Task<(IChatClient? Client, string? ModelId)> ResolveJudgeRuntimeAsync(
        ReviewVerificationContext? context,
        CancellationToken ct)
    {
        if (context?.Resolver is not null && context.ClientId != Guid.Empty)
        {
            try
            {
                var runtime = await context.Resolver
                    .ResolveChatRuntimeAsync(context.ClientId, AiPurpose.ReviewVerification, ct)
                    .ConfigureAwait(false);
                return (runtime.ChatClient, runtime.Model.RemoteModelId);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Fall through to the reviewer's own client.
            }
        }

        return (context?.ChatClient, context?.ModelId);
    }

    private async Task<VerificationOutcome> VerifyOneAsync(
        VerificationWorkItem workItem,
        ReviewVerificationContext? context,
        IChatClient? judgeClient,
        string? judgeModel,
        CancellationToken ct)
    {
        var claim = workItem.Claim;

        if (judgeClient is null || context?.Tools is null || string.IsNullOrWhiteSpace(claim.AnchorFilePath))
        {
            return ConservativeWithhold(claim);
        }

        try
        {
            var (windowStart, windowEnd) = ComputeAnchorWindow(claim.AnchorLineNumber);
            var anchorSource = await context.Tools
                .GetFileContentAsync(claim.AnchorFilePath!, context.SourceBranch, windowStart, windowEnd, ct)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(anchorSource))
            {
                return ConservativeWithhold(claim);
            }

            var (boundedSource, boundedStartLine) = BoundAnchorChars(anchorSource, windowStart, claim.AnchorLineNumber);

            var response = await judgeClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, BuildSystemPrompt()),
                    new ChatMessage(ChatRole.User, BuildUserMessage(claim, boundedSource, boundedStartLine)),
                ],
                new ChatOptions { ModelId = judgeModel },
                ct).ConfigureAwait(false);

            var verdict = TryParseVerdict(response.Text);
            if (verdict is { Confirmed: true })
            {
                return new VerificationOutcome(
                    claim.ClaimId,
                    claim.FindingId,
                    VerificationOutcome.SupportedKind,
                    FinalGateDecision.PublishDisposition,
                    [ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport],
                    [],
                    VerificationOutcome.ModerateEvidence,
                    Truncate(verdict.Reason, 280),
                    VerificationOutcome.AiMicroVerifierEvaluator,
                    false);
            }

            return ConservativeWithhold(claim);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Degraded-safe: any failure preserves the conservative withhold rather than risk a bad publish.
            return ConservativeWithhold(claim);
        }
    }

    // Reads a window centered on the claim's anchor line so a defect deep in a large file still reaches the
    // judge. When the anchor line is unknown we fall back to the file head, preserving the prior behavior.
    private static (int StartLine, int EndLine) ComputeAnchorWindow(int? anchorLineNumber)
    {
        if (anchorLineNumber is not int line || line <= 0)
        {
            return (1, MaxAnchorLines);
        }

        var start = Math.Max(1, line - MaxAnchorLines / 2);
        return (start, start + MaxAnchorLines - 1);
    }

    // Enforces the character ceiling while keeping the anchor line inside the window: an over-budget window
    // is sliced around the anchor (not from its head) so the cited code is never the part dropped. Returns
    // the bounded text together with the file line its first retained line corresponds to.
    private static (string Text, int StartLine) BoundAnchorChars(string source, int windowStartLine, int? anchorLineNumber)
    {
        if (source.Length <= MaxAnchorChars)
        {
            return (source, windowStartLine);
        }

        var sliceStart = 0;
        if (anchorLineNumber is int line && line >= windowStartLine)
        {
            var anchorOffset = OffsetOfLine(source, line - windowStartLine);
            sliceStart = Math.Clamp(anchorOffset - MaxAnchorChars / 2, 0, source.Length - MaxAnchorChars);
        }

        var slice = source.Substring(sliceStart, MaxAnchorChars);
        var retainedStartLine = windowStartLine + CountNewlines(source, 0, sliceStart);
        var prefix = sliceStart > 0 ? "…(truncated)\n" : string.Empty;
        var suffix = sliceStart + MaxAnchorChars < source.Length ? "\n…(truncated)" : string.Empty;
        return (prefix + slice + suffix, retainedStartLine);
    }

    // Character offset of the start of the line at the given zero-based index within the text.
    private static int OffsetOfLine(string text, int lineIndex)
    {
        if (lineIndex <= 0)
        {
            return 0;
        }

        var offset = 0;
        for (var seen = 0; seen < lineIndex; seen++)
        {
            var next = text.IndexOf('\n', offset);
            if (next < 0)
            {
                return text.Length;
            }

            offset = next + 1;
        }

        return offset;
    }

    private static int CountNewlines(string text, int start, int end)
    {
        var count = 0;
        for (var index = start; index < end; index++)
        {
            if (text[index] == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private static VerificationOutcome ConservativeWithhold(ClaimDescriptor claim)
    {
        return new VerificationOutcome(
            claim.ClaimId,
            claim.FindingId,
            VerificationOutcome.NonVerifiableKind,
            FinalGateDecision.SummaryOnlyDisposition,
            [ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport],
            [],
            VerificationOutcome.NoEvidence,
            "Evidence-backed verification could not confirm this claim from the anchor source.",
            VerificationOutcome.AiMicroVerifierEvaluator,
            false);
    }

    private static string BuildSystemPrompt()
    {
        return "You are a strict code-review verifier. You are given a CLAIM that a code change introduces a "
               + "defect, plus the current source of the file the claim concerns. Decide whether the claim is "
               + "CONFIRMED by the code as written. Confirm ONLY when the cited code clearly exhibits the asserted "
               + "defect and you can name the concrete line(s)/symbol that prove it. If the code does not support "
               + "the claim, the concern is hypothetical, or the provided source is insufficient to be sure, do "
               + "NOT confirm. Respond with ONLY a JSON object and nothing else: "
               + "{\"verdict\":\"confirmed|not_confirmed\",\"reason\":\"<one sentence; cite the line or symbol>\"}.";
    }

    private static string BuildUserMessage(ClaimDescriptor claim, string anchorSource, int sourceStartLine)
    {
        var subject = string.IsNullOrWhiteSpace(claim.SubjectIdentifier) ? "(none)" : claim.SubjectIdentifier;
        var anchorLine = claim.AnchorLineNumber?.ToString() ?? "(unknown)";
        var startLine = sourceStartLine.ToString();
        return $"CLAIM: {claim.AssertionText}\nSubject symbol: {subject}\nAnchor: {claim.AnchorFilePath}:{anchorLine}\n\n"
               + $"CURRENT SOURCE OF {claim.AnchorFilePath} (source branch, starting at line {startLine}):\n{anchorSource}";
    }

    private static ParsedVerdict? TryParseVerdict(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("verdict", out var verdictEl)
                                                       || verdictEl.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var confirmed = string.Equals(verdictEl.GetString()?.Trim(), "confirmed", StringComparison.OrdinalIgnoreCase);
            var reason = root.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String
                ? reasonEl.GetString() ?? string.Empty
                : string.Empty;
            return new ParsedVerdict(confirmed, reason);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string value, int max)
    {
        return string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
    }

    private sealed record ParsedVerdict(bool Confirmed, string Reason);
}
