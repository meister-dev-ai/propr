// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.AI;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;

/// <summary>
///     Evidence-gathering verifier for claims the deterministic verifier can only withhold for lack of
///     bounded evidence. For each work item it reads the anchor file via the review-context tools and asks a
///     skeptical bounded judging call whether the asserted defect is actually present in the code, whether the
///     behaviour is what the implementer intended according to the pull request, its linked work items and the code
///     comments, and whether intended behaviour contradicts a stated requirement or other functionality. A true
///     finding is published unless it is intended and contradicts nothing. It can only
///     PROMOTE a claim to publication when the evidence confirms it; on refusal, missing context, or any
///     failure it returns the same conservative withhold the deterministic verifier produces — so it never
///     reduces precision below the current behavior, it only recovers real findings the gate was discarding.
/// </summary>
/// <remarks>
///     When a protocol recorder and <see cref="ReviewVerificationContext.ProtocolId" /> are available, each judge call is
///     recorded as an AI call with its token usage and model, so the spend and the judging model appear in the trace.
/// </remarks>
public sealed class EvidenceBackedReviewVerifier(IProtocolRecorder? protocolRecorder = null) : IReviewFindingVerifier
{
    private const string JudgeCallName = "ai_call_evidence_verification";

    private const int MaxAnchorChars = 8000;
    private const int MaxAnchorLines = 400;
    private const string SystemPromptStageKey = "evidence_verification_system";
    private const string UserPromptStageKey = "evidence_verification_user";

    private static readonly HashSet<string> PlaceholderSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "n/a", "na", "null", "-", "unknown", "nothing", "not applicable",
    };

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
            return ConservativeWithhold(claim, "escalation skipped: no judge client, review tools, or anchor path available");
        }

        try
        {
            var (windowStart, windowEnd) = ComputeAnchorWindow(claim.AnchorLineNumber);
            var anchorSource = await context.Tools
                .GetFileContentAsync(claim.AnchorFilePath!, context.SourceBranch, windowStart, windowEnd, ct)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(anchorSource))
            {
                return ConservativeWithhold(claim, $"escalation degraded: anchor source empty for {claim.AnchorFilePath}");
            }

            var (boundedSource, boundedStartLine) = BoundAnchorChars(anchorSource, windowStart, claim.AnchorLineNumber);

            var systemPrompt = BuildSystemPrompt();
            var userMessage = BuildUserMessage(claim, boundedSource, boundedStartLine, context.Intent);
            var response = await judgeClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, systemPrompt),
                    new ChatMessage(ChatRole.User, userMessage),
                ],
                new ChatOptions { ModelId = judgeModel },
                ct).ConfigureAwait(false);

            await this.RecordJudgeCallAsync(context.ProtocolId, response, systemPrompt, userMessage, judgeModel, ct).ConfigureAwait(false);

            return ToOutcome(claim, TryParseVerdict(response.Text));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Degraded-safe: any failure preserves the conservative withhold rather than risk a bad publish,
            // but the cause is carried in the outcome so the recorded local decision shows what failed.
            return ConservativeWithhold(claim, $"escalation degraded: {ex.GetType().Name}: {Truncate(ex.Message, 160)}");
        }
    }

    // Records the judge call and adds its tokens to the protocol, as the PR-level verifier does for its calls. A recording
    // failure does not change the verification outcome.
    private async Task RecordJudgeCallAsync(
        Guid? protocolId,
        ChatResponse response,
        string systemPrompt,
        string userMessage,
        string? modelId,
        CancellationToken ct)
    {
        if (protocolRecorder is null || !protocolId.HasValue)
        {
            return;
        }

        try
        {
            var usage = AiTokenUsageExtractor.FromResponse(response);
            await protocolRecorder.RecordAiCallAsync(
                protocolId.Value,
                0,
                response.Usage?.InputTokenCount,
                response.Usage?.OutputTokenCount,
                userMessage,
                systemPrompt,
                response.Text,
                ct,
                JudgeCallName,
                cachedInputTokens: usage.IsEstimated ? null : usage.CachedInputTokens,
                cacheWriteTokens: usage.IsEstimated ? null : usage.CacheWriteTokens,
                reasoningTokens: usage.IsEstimated ? null : usage.ReasoningTokens).ConfigureAwait(false);
            await protocolRecorder.AddTokensAsync(
                protocolId.Value,
                usage.InputTokens,
                usage.OutputTokens,
                AiConnectionModelCategory.Default,
                modelId,
                ct,
                usage.CachedInputTokens,
                usage.CacheWriteTokens,
                usage.ReasoningTokens).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // The trace entry is lost; the verdict still applies.
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

    // Maps the judge verdict onto the local decision. A true finding is published unless the judge found the behaviour
    // intended and contradicting nothing; an intended finding that contradicts a named source is published with that
    // source in its summary. Every other response keeps the conservative withhold, so an unusable verdict never
    // publishes a finding.
    private static VerificationOutcome ToOutcome(ClaimDescriptor claim, ParsedVerdict? verdict)
    {
        if (verdict is null)
        {
            return ConservativeWithhold(claim, "escalation degraded: judge response was not a parseable verdict", EvidenceJudgeVerdicts.Unparseable);
        }

        switch (verdict.Kind)
        {
            case EvidenceJudgeVerdicts.Confirmed:
                return Publish(claim, Truncate(verdict.Reason, 280), EvidenceJudgeVerdicts.Confirmed);
            case EvidenceJudgeVerdicts.IntendedContradicted when NamesASource(verdict.Contradicts):
                return Publish(
                    claim,
                    $"Intended behaviour that contradicts {Truncate(verdict.Contradicts.Trim(), 120)}: {Truncate(verdict.Reason, 240)}",
                    EvidenceJudgeVerdicts.IntendedContradicted);
            case EvidenceJudgeVerdicts.IntendedContradicted:
                return ConservativeWithhold(
                    claim, "escalation degraded: the contradiction verdict named no contradicted source", EvidenceJudgeVerdicts.Unparseable);
            case EvidenceJudgeVerdicts.Intended:
                return ConservativeWithhold(
                    claim, $"judge found the behaviour intended and contradicting nothing: {Truncate(verdict.Reason, 200)}", EvidenceJudgeVerdicts.Intended);
            case EvidenceJudgeVerdicts.NotConfirmed:
                return ConservativeWithhold(claim, $"judge did not confirm: {Truncate(verdict.Reason, 200)}", EvidenceJudgeVerdicts.NotConfirmed);
            default:
                return ConservativeWithhold(
                    claim, $"escalation degraded: judge returned the unknown verdict '{Truncate(verdict.Kind, 40)}'", EvidenceJudgeVerdicts.Unparseable);
        }
    }

    private static VerificationOutcome Publish(ClaimDescriptor claim, string summary, string judgeVerdict)
    {
        return new VerificationOutcome(
            claim.ClaimId,
            claim.FindingId,
            VerificationOutcome.SupportedKind,
            FinalGateDecision.PublishDisposition,
            [ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport],
            [],
            VerificationOutcome.ModerateEvidence,
            summary,
            VerificationOutcome.AiMicroVerifierEvaluator,
            false)
        {
            JudgeVerdict = judgeVerdict,
        };
    }

    private static VerificationOutcome ConservativeWithhold(ClaimDescriptor claim, string? cause = null, string? judgeVerdict = null)
    {
        var summary = string.IsNullOrWhiteSpace(cause)
            ? "Evidence-backed verification could not confirm this claim from the anchor source."
            : $"Evidence-backed verification could not confirm this claim from the anchor source ({cause}).";
        return new VerificationOutcome(
            claim.ClaimId,
            claim.FindingId,
            VerificationOutcome.NonVerifiableKind,
            FinalGateDecision.SummaryOnlyDisposition,
            [ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport],
            [],
            VerificationOutcome.NoEvidence,
            summary,
            VerificationOutcome.AiMicroVerifierEvaluator,
            false)
        {
            JudgeVerdict = judgeVerdict,
        };
    }

    private static string BuildSystemPrompt()
    {
        return PromptTemplateRuntime.RenderStage(SystemPromptStageKey);
    }

    private static string BuildUserMessage(ClaimDescriptor claim, string anchorSource, int sourceStartLine, ReviewVerificationIntent? intent)
    {
        return PromptTemplateRuntime.RenderStage(UserPromptStageKey, EvidenceJudgeInput.BuildUserModel(claim, anchorSource, sourceStartLine, intent));
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

            return new ParsedVerdict(NormalizeVerdictKind(verdictEl.GetString()), ReadString(root, "reason"), ReadString(root, "contradicts"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Accepts the spelling variants a model produces for the same verdict ("Intended-Contradicted", "not confirmed",
    // "confirmed.") and maps them onto the canonical snake_case kind.
    private static string NormalizeVerdictKind(string? verdict)
    {
        return (verdict ?? string.Empty).Trim().TrimEnd('.').Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
    }

    // A contradiction verdict publishes only when it names the contradicted source. Placeholder values and phrases
    // that deny a contradiction ("none found", "no contradiction") name none.
    private static bool NamesASource(string contradicts)
    {
        var value = contradicts.Trim().TrimEnd('.').Trim();
        return value.Length > 0
               && !PlaceholderSources.Contains(value)
               && !value.StartsWith("none ", StringComparison.OrdinalIgnoreCase)
               && !value.StartsWith("no ", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string Truncate(string value, int max)
    {
        return string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
    }

    private sealed record ParsedVerdict(string Kind, string Reason, string Contradicts);
}
