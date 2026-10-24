// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.AI;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;

public sealed partial class EvidenceBackedReviewVerifier
{
    private const int MaxConfidenceResponseCharacters = 64000;
    private const int MaxSuggestedFixCharacters = 12000;
    private const string ContradictionCheckCallName = "ai_call_confidence_contradiction_check";

    // An intermediate model call of the contradiction check, in which the checking model asked for lookups.
    private const string ContradictionCheckToolTurnCallName = "ai_call_confidence_contradiction_check_tool_turn";

    private static readonly Lazy<Func<object?, string>> AdditionalInputsTemplate =
        PromptTemplateRuntime.PrepareTemplateFile("shared/evidence-verification-additional-inputs.hbs");

    private static readonly Lazy<Func<object?, string>> SourceLookupsTemplate =
        PromptTemplateRuntime.PrepareTemplateFile("shared/evidence-verification-source-lookups.hbs");

    private static readonly Lazy<Func<object?, string>> ContradictionSystemTemplate =
        PromptTemplateRuntime.PrepareTemplateFile("shared/evidence-verification-contradiction-system.hbs");

    private static readonly Lazy<string> ContradictionSystemText = new(() =>
        ContradictionSystemTemplate.Value(new { baseSystemPrompt = BuildSystemPrompt() }));

    // The tool binder reads the line number from a JSON number or from a string that holds one, and the tool labels the
    // returned lines from that number, so the labels are removed from the same line on. Any other value makes the tool
    // start at line 1.
    private static int ReadStartLine(JsonElement start)
    {
        var line = start.ValueKind switch
        {
            JsonValueKind.Number when start.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(start.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 1,
        };
        return Math.Max(1, line);
    }

    // Keeps the successful source-branch reads of the first judge and of the contradiction check, so the source quotes of
    // each answer can be grounded in code that the judging model read.
    private static void CaptureSourceLookupEvidence(JudgeToolCall call, string sourceBranch, List<JudgeExcerpt> sources)
    {
        if (!call.Succeeded || call.ToolName != "get_file_content" || call.ResultText is not { Length: > 0 } text
            || sources.Count >= EvidenceJudgeToolLoop.MaxToolCallsPerClaim)
        {
            return;
        }

        try
        {
            using var arguments = JsonDocument.Parse(call.Arguments);
            var root = arguments.RootElement;
            if (root.ValueKind != JsonValueKind.Object || ReadString(root, "branch") != sourceBranch || BoundedEvidenceText(root, "path") is not { } path)
            {
                return;
            }

            var startLine = root.TryGetProperty("startLine", out var start) ? ReadStartLine(start) : 1;
            // AIFunction may return a JSON string scalar; compare source bytes after decoding that transport wrapper.
            if (text.StartsWith('"'))
            {
                using var result = JsonDocument.Parse(text);
                if (result.RootElement.ValueKind != JsonValueKind.String)
                {
                    return;
                }

                text = result.RootElement.GetString() ?? string.Empty;
            }

            var lines = text.ReplaceLineEndings("\n").Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                var prefix = $"{(long)startLine + index} | ";
                var labelStart = 0;
                while (labelStart < lines[index].Length && lines[index][labelStart] == ' ')
                {
                    labelStart++;
                }

                if (lines[index].AsSpan(labelStart).StartsWith(prefix, StringComparison.Ordinal))
                {
                    lines[index] = lines[index][(labelStart + prefix.Length)..];
                }
            }

            var source = string.Join('\n', lines);
            sources.Add(
                new JudgeExcerpt(path, startLine, (int)Math.Min(int.MaxValue, (long)startLine + lines.Length - 1), source, JudgeExcerpt.SupportingFileOrigin));
        }
        catch (JsonException)
        {
            // Truncated tool arguments cannot establish source provenance for confidence.
        }
    }

    private static string BuildAdditionalInputs(VerificationWorkItem workItem)
    {
        // Structured inputs are data. Neither the producer's confidence nor its rationale is sent to the judge.
        var boundary = Guid.NewGuid().ToString("N");
        var fix = workItem.SuggestedFix;
        var input = JsonSerializer.Serialize(
            new
            {
                suggestedFix = fix is null
                    ? null
                    : new
                    {
                        fix.StartLine, fix.EndLine,
                        originalText = fix.OriginalText is { Length: <= MaxSuggestedFixCharacters } ? fix.OriginalText : "(omitted: size limit)",
                        replacementText = fix.ReplacementText is { Length: <= MaxSuggestedFixCharacters } ? fix.ReplacementText : "(omitted: size limit)",
                        fix.RejectionReason,
                    },
                synthesisChallenge = workItem.Challenge,
            });
        return AdditionalInputsTemplate.Value(new { boundary, inputJson = input });
    }

    private static string BuildSourceLookupInputs(IReadOnlyList<JudgeExcerpt> sources) => sources.Count == 0
        ? string.Empty
        : SourceLookupsTemplate.Value(new { sourcesJson = JsonSerializer.Serialize(sources) });

    internal static string BuildContradictionSystemPrompt() => ContradictionSystemText.Value;

    // Runs an independent contradiction check on the runtime that contradictionJudge resolves: the low-effort model of the
    // first judge for the check that every confirmed Error and Warning receives, because its cost grows with the number of
    // findings, or the high-effort model for an escalation. Its prompt asks it to disprove the claim, and it does not see
    // the first verdict. It has the same repository lookups as the first judge, so it can
    // open the caller, guard, registration or test that decides the claim, and it gets its own time window, because it
    // starts after the first judgment has used part of the claim's limit. A finding the check does not confirm takes the
    // disposition that the first judge's mapping gives the check's verdict, so a confirmation stands only when both
    // judgments support it. A check that fails, times out or answers unusably keeps the first judgment.
    private async Task<VerificationOutcome> CheckContradictionAsync(
        VerificationWorkItem workItem,
        VerificationOutcome initial,
        ReviewVerificationContext context,
        JudgeRuntime initialJudge,
        Lazy<Task<JudgeRuntime>> contradictionJudge,
        ClaimRecording claimRecording,
        string userMessage,
        string groundingSource,
        IReadOnlyList<JudgeExcerpt> excerpts,
        bool producedWithRepositoryTools)
    {
        var runtime = initialJudge;
        var recording = claimRecording with { LockDeadline = DateTimeOffset.UtcNow + this._judgeCallTimeout + this._recordingGrace };
        using var window = CancellationTokenSource.CreateLinkedTokenSource(recording.RunToken);
        window.CancelAfter(this._judgeCallTimeout);
        var checkToken = window.Token;
        try
        {
            runtime = await contradictionJudge.Value.WaitAsync(checkToken).ConfigureAwait(false);
            if (runtime.Client is null)
            {
                return initial with { ContradictionCheckStatus = "unavailable", ContradictionCheckRuntime = runtime.Provenance };
            }

            var claim = workItem.Claim;
            var system = BuildContradictionSystemPrompt();
            var options = new ChatOptions { ModelId = runtime.ModelId, MaxOutputTokens = this._outputBudgets.Initial };
            if (this._verificationReasoningEffort != ReviewReasoningEffort.None)
            {
                options.ApplyResolvedReasoning(false, this._verificationReasoningEffort);
            }

            var checkSources = new List<JudgeExcerpt>();
            var result = await EvidenceJudgeToolLoop.RunAsync(
                runtime.Client,
                options,
                this._outputBudgets.Retry,
                HasUsableVerdict,
                system,
                userMessage,
                EvidenceJudgeToolLoop.BuildJudgeTools(context.Tools!, checkToken),
                (modelResponse, isFinal) => this.RecordSerializedAsync(
                    recording, claim,
                    token => this.RecordJudgeCallAsync(
                        context.ProtocolId, modelResponse, system, userMessage, runtime,
                        isFinal ? ContradictionCheckCallName : ContradictionCheckToolTurnCallName, token, claim)),
                toolCall =>
                {
                    CaptureSourceLookupEvidence(toolCall, context.SourceBranch, checkSources);
                    return this.RecordSerializedAsync(
                        recording, claim,
                        token => this.RecordJudgeToolCallAsync(context.ProtocolId, claim, toolCall, token, ContradictionCheckLookupStage));
                },
                outputLimit => this.RecordSerializedAsync(
                    recording, claim, token => this.RecordJudgeOutputLimitAsync(context.ProtocolId, claim, runtime, outputLimit, token)),
                checkToken).ConfigureAwait(false);

            var response = result.Response;
            var verdict = TryParseVerdict(response.Text);
            var evidence = ReadConfidenceEvidence(response.Text, claim, groundingSource, [.. excerpts, .. checkSources]);
            var usable = !result.OutputLimitReached && (response.FinishReason is not { } finish || finish != ChatFinishReason.Length);
            usable &= ProviderError(response) is null && UnusableResponseOutcome(claim, response) is null;
            if (usable && verdict?.Kind == EvidenceJudgeVerdicts.NotConfirmed && evidence is { SourceGrounded: true }
                && !string.IsNullOrWhiteSpace(evidence.Counterevidence))
            {
                return new VerificationOutcome(
                    claim.ClaimId, claim.FindingId, VerificationOutcome.ContradictedKind,
                    FinalGateDecision.DropDisposition, ["source_counterevidence_refutes_claim"], [], VerificationOutcome.StrongEvidence,
                    Truncate(verdict.Reason + " " + evidence.Counterevidence, VerificationConfidenceEvidence.MaxTextLength),
                    VerificationOutcome.CombinedEvaluator, false)
                {
                    JudgeVerdict = verdict.Kind, Runtime = initial.Runtime, ContradictionCheckRuntime = runtime.Provenance,
                    ContradictionCheckStatus = "refuted", ConfidenceEvidence = evidence,
                };
            }

            if (usable && verdict is not null
                       && ToOutcome(claim, verdict, producedWithRepositoryTools, evidence) is var held
                       && held.RecommendedDisposition != FinalGateDecision.PublishDisposition)
            {
                return held with
                {
                    Runtime = initial.Runtime,
                    ContradictionCheckRuntime = runtime.Provenance,
                    ContradictionCheckStatus = "withheld",
                    ConfidenceEvidence = evidence ?? initial.ConfidenceEvidence,
                };
            }

            if (usable && IsSupportedVerdict(verdict)
                       && TryGetSupportedConfidenceEvidence(initial.ConfidenceEvidence, evidence, out var primary, out var secondary))
            {
                var level = (FindingConfidence)Math.Min((int)primary.Level, (int)secondary.Level);
                var assumptions = primary.Assumptions.Concat(secondary.Assumptions).Distinct(StringComparer.Ordinal)
                    .Take(VerificationConfidenceEvidence.MaxAssumptions).ToArray();
                var complete = assumptions.Length == 0 && !string.IsNullOrWhiteSpace(primary.Counterevidence)
                                                       && !string.IsNullOrWhiteSpace(secondary.Counterevidence);

                // The published reason explains the published level, so it comes from the judgment that set that level.
                var limiting = secondary.Level < primary.Level ? secondary : primary;
                return initial with
                {
                    ConfidenceEvidence = limiting with { Level = level, Assumptions = assumptions, ContradictionCheckPassed = complete },
                    ContradictionCheckStatus = complete ? "supported" : "unresolved_assumptions",
                    ContradictionCheckRuntime = runtime.Provenance,
                };
            }

            return initial with { ContradictionCheckStatus = usable ? "unresolved" : "unusable_response", ContradictionCheckRuntime = runtime.Provenance };
        }
        catch (Exception ex) when (!recording.RunToken.IsCancellationRequested)
        {
            // Failure of the additional confidence check does not invalidate an independently supported finding.
            return initial with
            {
                ContradictionCheckStatus = checkToken.IsCancellationRequested ? "timeout" : "failed:" + ex.GetType().Name,
                ContradictionCheckRuntime = runtime.Provenance
            };
        }
    }

    private static VerificationConfidenceEvidence? ReadConfidenceEvidence(
        string? text, ClaimDescriptor claim, string anchorSource,
        IReadOnlyList<JudgeExcerpt> excerpts)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxConfidenceResponseCharacters)
        {
            return null;
        }

        try
        {
            using var doc = JudgeResponseJson.TryParseObject(text);
            if (doc is null || !doc.RootElement.TryGetProperty("confidence_evidence", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var levelText = ReadString(data, "level");
            var level = Enum.GetValues<FindingConfidence>().FirstOrDefault(value => value.ToString().Equals(levelText, StringComparison.OrdinalIgnoreCase));
            var witness = BoundedEvidenceText(data, "witness");
            var consequence = BoundedEvidenceText(data, "consequence");
            var counterevidence = BoundedEvidenceText(data, "counterevidence");
            if (witness is null || consequence is null || counterevidence is null
                || !data.TryGetProperty("assumptions", out var assumptionsElement) || assumptionsElement.ValueKind != JsonValueKind.Array
                || assumptionsElement.GetArrayLength() > VerificationConfidenceEvidence.MaxAssumptions)
            {
                return null;
            }

            var assumptions = new List<string>();
            foreach (var assumption in assumptionsElement.EnumerateArray())
            {
                if (assumption.ValueKind != JsonValueKind.String || !VerificationConfidenceEvidence.IsValidText(assumption.GetString()))
                {
                    return null;
                }

                assumptions.Add(assumption.GetString()!);
            }

            var grounded = false;
            if (data.TryGetProperty("source_quotes", out var quotes) && quotes.ValueKind == JsonValueKind.Array
                                                                     && quotes.GetArrayLength() is > 0 and <= VerificationConfidenceEvidence.MaxSourceQuotes)
            {
                grounded = quotes.EnumerateArray().All(quote => IsGroundedQuote(quote, claim, anchorSource, excerpts));
            }

            var covered = data.TryGetProperty("all_claims_covered", out var all) && all.ValueKind == JsonValueKind.True;
            return new VerificationConfidenceEvidence(level, witness, consequence, counterevidence, assumptions, covered, grounded);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsSupportedVerdict(ParsedVerdict? verdict) =>
        verdict?.Kind is EvidenceJudgeVerdicts.Confirmed or EvidenceJudgeVerdicts.IntendedContradicted;

    private static bool TryGetSupportedConfidenceEvidence(
        VerificationConfidenceEvidence? initial,
        VerificationConfidenceEvidence? contradiction,
        [NotNullWhen(true)] out VerificationConfidenceEvidence? primary,
        [NotNullWhen(true)] out VerificationConfidenceEvidence? secondary)
    {
        primary = initial;
        secondary = contradiction;
        return primary?.HasCompleteEvidence() == true && secondary?.HasCompleteEvidence() == true;
    }

    private static string? BoundedEvidenceText(JsonElement data, string key)
    {
        if (!data.TryGetProperty(key, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        return VerificationConfidenceEvidence.IsValidText(value) ? value : null;
    }

    private static bool IsGroundedQuote(JsonElement data, ClaimDescriptor claim, string anchorSource, IReadOnlyList<JudgeExcerpt> excerpts)
    {
        if (data.ValueKind != JsonValueKind.Object || BoundedEvidenceText(data, "file_path") is not { } path
                                                   || BoundedEvidenceText(data, "quote") is not { } quote)
        {
            return false;
        }

        var normalized = quote.ReplaceLineEndings("\n").Trim();
        if (normalized.Length == 1 || !normalized.Any(char.IsLetterOrDigit))
        {
            return false;
        }

        return path == claim.AnchorFilePath && ContainsCompleteSourceLines(anchorSource, normalized)
               || excerpts.Any(excerpt => excerpt.Path == path && ContainsCompleteSourceLines(excerpt.Text, normalized));
    }

    private static bool ContainsCompleteSourceLines(string source, string quote)
    {
        source = source.ReplaceLineEndings("\n");
        var searchStart = 0;
        while (source.IndexOf(quote, searchStart, StringComparison.Ordinal) is var start && start >= 0)
        {
            var end = start + quote.Length;
            var lineStart = start == 0 ? 0 : source.LastIndexOf('\n', start - 1) + 1;
            var lineEnd = source.IndexOf('\n', end);
            if (lineEnd < 0)
            {
                lineEnd = source.Length;
            }

            if (source.AsSpan(lineStart, start - lineStart).Trim().IsEmpty && source.AsSpan(end, lineEnd - end).Trim().IsEmpty)
            {
                return true;
            }

            searchStart = start + 1;
        }

        return false;
    }
}
