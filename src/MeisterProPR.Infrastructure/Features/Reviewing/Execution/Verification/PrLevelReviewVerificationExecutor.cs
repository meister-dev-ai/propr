// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.AI;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;

/// <summary>
///     Verifies synthesized PR-level findings against bounded repository evidence before they enter the
///     deterministic final gate. This stage extracts the strongest cross-file claim, gathers repository
///     evidence, optionally invokes the PR micro-verifier model, records verification protocol events,
///     and returns findings annotated with their verification outcomes.
/// </summary>
internal sealed class PrLevelReviewVerificationExecutor(
    IReviewClaimExtractor? reviewClaimExtractor,
    IReviewEvidenceCollector? reviewEvidenceCollector,
    IProtocolRecorder protocolRecorder,
    AiReviewOptions options)
{
    private static readonly JsonSerializerOptions FinalGateJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     Applies bounded PR-level verification to synthesized cross-file findings.
    /// </summary>
    public async Task<IReadOnlyList<CandidateReviewFinding>> ApplyAsync(
        IReadOnlyList<CandidateReviewFinding> synthesizedFindings,
        ReviewSystemContext reviewContext,
        string sourceBranch,
        Guid? protocolId,
        IChatClient? fallbackChatClient,
        CancellationToken ct)
    {
        if (synthesizedFindings.Count == 0 || reviewClaimExtractor is null || reviewEvidenceCollector is null)
        {
            return synthesizedFindings;
        }

        var prVerificationClient = reviewContext.DefaultReviewChatClient ?? reviewContext.TierChatClient ?? fallbackChatClient;
        var prVerificationModelId = reviewContext.DefaultReviewModelId ?? reviewContext.ModelId ?? options.ModelId;
        var verified = new List<CandidateReviewFinding>(synthesizedFindings.Count);

        foreach (var finding in synthesizedFindings)
        {
            IReadOnlyList<ClaimDescriptor> claims;
            try
            {
                claims = reviewClaimExtractor.ExtractClaims(finding);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                await this.RecordDegradedAsync(
                    protocolId,
                    new
                    {
                        findingId = finding.FindingId,
                        stage = ClaimDescriptor.PrLevelStage,
                        degradedComponent = "claim_extraction",
                    },
                    null,
                    ex.Message,
                    ct);

                verified.Add(CreateClaimExtractionDegradedFinding(finding, ex.Message));
                continue;
            }

            if (claims.Count == 0)
            {
                verified.Add(finding);
                continue;
            }

            var claim = claims[0];
            var initialWorkItem = new VerificationWorkItem(
                claim,
                finding.Provenance,
                claim.Stage,
                VerificationWorkItem.CrossFileScope,
                true,
                finding.Evidence);

            EvidenceBundle evidence;
            try
            {
                evidence = await reviewEvidenceCollector.CollectEvidenceAsync(initialWorkItem, reviewContext.ReviewTools, sourceBranch, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                await this.RecordDegradedAsync(
                    protocolId,
                    new
                    {
                        findingId = finding.FindingId,
                        claimId = claim.ClaimId,
                        stage = ClaimDescriptor.PrLevelStage,
                        degradedComponent = "evidence_collection",
                    },
                    null,
                    ex.Message,
                    ct);

                verified.Add(CreateEvidenceCollectionDegradedFinding(finding, claim, ex.Message));
                continue;
            }

            await this.RecordEvidenceCollectedAsync(protocolId, finding, claim, evidence, ct);

            var updatedEvidence = BuildUpdatedEvidence(finding, evidence);
            var evidenceBackedWorkItem = new VerificationWorkItem(
                claim,
                finding.Provenance,
                claim.Stage,
                VerificationWorkItem.CrossFileScope,
                true,
                updatedEvidence);

            var outcome = await this.VerifyClaimAsync(
                finding,
                claim,
                evidence,
                updatedEvidence,
                evidenceBackedWorkItem,
                reviewContext,
                prVerificationClient,
                prVerificationModelId,
                protocolId,
                ct);

            await this.RecordDecisionAsync(protocolId, outcome, evidenceBackedWorkItem, ct);

            verified.Add(
                new CandidateReviewFinding(
                    finding.FindingId,
                    finding.Provenance,
                    finding.Severity,
                    finding.Message,
                    finding.Category,
                    finding.FilePath,
                    finding.LineNumber,
                    evidenceBackedWorkItem.ExistingEvidence,
                    finding.CandidateSummaryText,
                    finding.InvariantCheckContext,
                    outcome));
        }

        return verified;
    }

    private async Task<VerificationOutcome> VerifyClaimAsync(
        CandidateReviewFinding finding,
        ClaimDescriptor claim,
        EvidenceBundle evidence,
        EvidenceReference updatedEvidence,
        VerificationWorkItem evidenceBackedWorkItem,
        ReviewSystemContext reviewContext,
        IChatClient? prVerificationClient,
        string? prVerificationModelId,
        Guid? protocolId,
        CancellationToken ct)
    {
        if (prVerificationClient is null)
        {
            return new VerificationOutcome(
                claim.ClaimId,
                claim.FindingId,
                VerificationOutcome.UnresolvedKind,
                FinalGateDecision.SummaryOnlyDisposition,
                [
                    updatedEvidence.HasResolvedMultiFileEvidence
                        ? ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport
                        : ReviewFindingGateReasonCodes.MissingMultiFileEvidence,
                ],
                [],
                VerificationOutcome.WeakEvidence,
                "Retrieved context is treated as a verification hint until a bounded claim outcome supports publication.",
                VerificationOutcome.AiMicroVerifierEvaluator,
                false);
        }

        var systemPrompt = ReviewPrompts.BuildPrVerificationSystemPrompt(reviewContext);
        var userMessage = ReviewPrompts.BuildPrVerificationUserMessage(claim, evidence);

        try
        {
            var response = await prVerificationClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, systemPrompt),
                    new ChatMessage(ChatRole.User, userMessage),
                ],
                new ChatOptions { ModelId = prVerificationModelId, Temperature = reviewContext.Temperature },
                ct);

            var responseText = response.Text ?? string.Empty;
            await this.RecordAiUsageAsync(protocolId, response, userMessage, systemPrompt, responseText, prVerificationModelId, ct);

            if (TryParsePrVerificationResponse(responseText, claim, out var outcome))
            {
                return outcome;
            }

            await this.RecordDegradedAsync(
                protocolId,
                new
                {
                    findingId = finding.FindingId,
                    claimId = claim.ClaimId,
                    stage = ClaimDescriptor.PrLevelStage,
                    degradedComponent = "bounded_ai_response_parse",
                },
                responseText,
                "PR-level verification response could not be parsed.",
                ct);

            return VerificationOutcome.DegradedUnresolved(
                claim,
                VerificationOutcome.AiMicroVerifierEvaluator,
                ReviewFindingGateReasonCodes.VerificationDegraded,
                "AI micro-verification degraded: response could not be parsed.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await this.RecordDegradedAsync(
                protocolId,
                new
                {
                    findingId = finding.FindingId,
                    claimId = claim.ClaimId,
                    stage = ClaimDescriptor.PrLevelStage,
                    degradedComponent = "bounded_ai_verification",
                },
                null,
                ex.Message,
                ct);

            return VerificationOutcome.DegradedUnresolved(
                claim,
                VerificationOutcome.AiMicroVerifierEvaluator,
                ReviewFindingGateReasonCodes.VerificationDegraded,
                $"AI micro-verification degraded: {ex.Message}");
        }
    }

    private async Task RecordEvidenceCollectedAsync(
        Guid? protocolId,
        CandidateReviewFinding finding,
        ClaimDescriptor claim,
        EvidenceBundle evidence,
        CancellationToken ct)
    {
        if (!protocolId.HasValue)
        {
            return;
        }

        await protocolRecorder.RecordVerificationEventAsync(
            protocolId.Value,
            ReviewProtocolEventNames.VerificationEvidenceCollected,
            JsonSerializer.Serialize(
                new
                {
                    findingId = finding.FindingId,
                    claimId = claim.ClaimId,
                    coverageState = evidence.CoverageState,
                }),
            JsonSerializer.Serialize(evidence, FinalGateJsonOptions),
            null,
            ct);
    }

    private async Task RecordDecisionAsync(
        Guid? protocolId,
        VerificationOutcome outcome,
        VerificationWorkItem workItem,
        CancellationToken ct)
    {
        if (!protocolId.HasValue)
        {
            return;
        }

        await protocolRecorder.RecordVerificationEventAsync(
            protocolId.Value,
            ReviewProtocolEventNames.VerificationPrDecision,
            JsonSerializer.Serialize(
                new
                {
                    findingId = outcome.FindingId,
                    claimId = outcome.ClaimId,
                    verifierFamilies = workItem.VerifierFamilies,
                }),
            JsonSerializer.Serialize(outcome, FinalGateJsonOptions),
            null,
            ct);
    }

    private async Task RecordAiUsageAsync(
        Guid? protocolId,
        ChatResponse response,
        string userMessage,
        string systemPrompt,
        string responseText,
        string? modelId,
        CancellationToken ct)
    {
        if (!protocolId.HasValue)
        {
            return;
        }

        await protocolRecorder.RecordAiCallAsync(
            protocolId.Value,
            0,
            response.Usage?.InputTokenCount,
            response.Usage?.OutputTokenCount,
            userMessage,
            systemPrompt,
            responseText,
            ct,
            "ai_call_pr_verification");

        await protocolRecorder.AddTokensAsync(
            protocolId.Value,
            response.Usage?.InputTokenCount ?? 0,
            response.Usage?.OutputTokenCount ?? 0,
            AiConnectionModelCategory.Default,
            modelId,
            ct);
    }

    private async Task RecordDegradedAsync(
        Guid? protocolId,
        object details,
        string? output,
        string? error,
        CancellationToken ct)
    {
        if (!protocolId.HasValue)
        {
            return;
        }

        await protocolRecorder.RecordVerificationEventAsync(
            protocolId.Value,
            ReviewProtocolEventNames.VerificationDegraded,
            JsonSerializer.Serialize(details),
            output,
            error,
            ct);
    }

    private static CandidateReviewFinding CreateClaimExtractionDegradedFinding(CandidateReviewFinding finding, string? error)
    {
        return new CandidateReviewFinding(
            finding.FindingId,
            finding.Provenance,
            finding.Severity,
            finding.Message,
            finding.Category,
            finding.FilePath,
            finding.LineNumber,
            finding.Evidence,
            finding.CandidateSummaryText,
            finding.InvariantCheckContext,
            VerificationOutcome.DegradedUnresolved(
                finding.FindingId,
                VerificationOutcome.DeterministicRulesEvaluator,
                ReviewFindingGateReasonCodes.VerificationDegraded,
                $"PR-level claim extraction degraded: {error}"));
    }

    private static CandidateReviewFinding CreateEvidenceCollectionDegradedFinding(
        CandidateReviewFinding finding,
        ClaimDescriptor claim,
        string? error)
    {
        return new CandidateReviewFinding(
            finding.FindingId,
            finding.Provenance,
            finding.Severity,
            finding.Message,
            finding.Category,
            finding.FilePath,
            finding.LineNumber,
            finding.Evidence,
            finding.CandidateSummaryText,
            finding.InvariantCheckContext,
            VerificationOutcome.DegradedUnresolved(
                claim,
                VerificationOutcome.AiMicroVerifierEvaluator,
                ReviewFindingGateReasonCodes.VerificationDegraded,
                $"PR-level evidence collection degraded: {error}"));
    }

    private static EvidenceReference BuildUpdatedEvidence(CandidateReviewFinding finding, EvidenceBundle evidence)
    {
        var supportingFiles = evidence.EvidenceItems
            .Select(item => item.SourceId)
            .Where(sourceId => !string.IsNullOrWhiteSpace(sourceId))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var evidenceState = evidence.CoverageState switch
        {
            EvidenceBundle.CompleteCoverage => EvidenceReference.ResolvedState,
            EvidenceBundle.PartialCoverage => EvidenceReference.PartialState,
            _ => EvidenceReference.MissingState,
        };

        return finding.Evidence is null
            ? new EvidenceReference([], supportingFiles, evidenceState, "review_context_tools")
            : new EvidenceReference(
                finding.Evidence.SupportingFindingIds,
                supportingFiles.Length > 0 ? supportingFiles : finding.Evidence.SupportingFiles,
                evidenceState,
                finding.Evidence.EvidenceSource);
    }

    private static bool TryParsePrVerificationResponse(
        string? responseText,
        ClaimDescriptor claim,
        out VerificationOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(claim);

        outcome = VerificationOutcome.DegradedUnresolved(
            claim,
            VerificationOutcome.AiMicroVerifierEvaluator,
            ReviewFindingGateReasonCodes.VerificationDegraded,
            "AI micro-verification degraded: response could not be parsed.");

        if (string.IsNullOrWhiteSpace(responseText))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(StripMarkdownCodeFences(responseText));
            var root = doc.RootElement;

            if (!root.TryGetProperty("verdict", out var verdictEl) ||
                !root.TryGetProperty("recommended_disposition", out var dispositionEl))
            {
                return false;
            }

            var verdict = verdictEl.GetString();
            var disposition = dispositionEl.GetString();
            if (string.IsNullOrWhiteSpace(verdict) || string.IsNullOrWhiteSpace(disposition))
            {
                return false;
            }

            var reasonCodes = root.TryGetProperty("reason_codes", out var reasonCodesEl) && reasonCodesEl.ValueKind == JsonValueKind.Array
                ? reasonCodesEl.EnumerateArray()
                    .Where(element => element.ValueKind == JsonValueKind.String)
                    .Select(element => element.GetString())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Cast<string>()
                    .ToArray()
                : [];

            if (reasonCodes.Length == 0)
            {
                reasonCodes =
                [
                    string.Equals(disposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal)
                        ? ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport
                        : ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport,
                ];
            }

            var summary = root.TryGetProperty("summary", out var summaryEl)
                ? summaryEl.GetString()
                : null;
            var normalizedDisposition = string.Equals(disposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal)
                ? FinalGateDecision.PublishDisposition
                : FinalGateDecision.SummaryOnlyDisposition;
            var normalizedVerdict = string.Equals(verdict, "supported", StringComparison.OrdinalIgnoreCase)
                ? VerificationOutcome.SupportedKind
                : VerificationOutcome.UnresolvedKind;
            var evidenceStrength = string.Equals(normalizedDisposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal)
                ? VerificationOutcome.StrongEvidence
                : VerificationOutcome.WeakEvidence;

            outcome = new VerificationOutcome(
                claim.ClaimId,
                claim.FindingId,
                normalizedVerdict,
                normalizedDisposition,
                reasonCodes,
                [],
                evidenceStrength,
                summary,
                VerificationOutcome.AiMicroVerifierEvaluator,
                false);

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string StripMarkdownCodeFences(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline >= 0)
        {
            trimmed = trimmed[(firstNewline + 1)..];
        }
        else
        {
            var braceStart = trimmed.IndexOf('{');
            if (braceStart >= 0)
            {
                trimmed = trimmed[braceStart..];
            }
        }

        var closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFence >= 0)
        {
            trimmed = trimmed[..closingFence];
        }

        return trimmed.Trim();
    }
}
