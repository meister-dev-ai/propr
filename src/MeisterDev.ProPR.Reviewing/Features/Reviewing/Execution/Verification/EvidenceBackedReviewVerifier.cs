// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;

/// <summary>
///     Evidence-gathering judge for claims the deterministic verifier can only withhold for lack of bounded evidence.
///     On the file-by-file path this is every finding that no invariant fact contradicts. The judge runs on the model
///     resolved for <see cref="AiPurpose.ReviewLowEffort" />, because it makes one call per finding. For each work item
///     it reads the anchor file through the review-context tools and asks whether the asserted defect is present in the
///     code, whether it leads to a concrete consequence that a reviewer would act on, whether the behaviour is what the
///     implementer intended according to the pull request, its linked work items
///     and the code comments, and whether intended behaviour contradicts a stated requirement or other functionality. A
///     true and actionable finding is published unless it is intended and contradicts nothing. When the judge answers that the code it
///     needs is not in its inputs, the finding is published only if the pass that produced it could read the repository.
///     On refusal, an unusable answer or any failure the verifier returns the conservative withhold of the
///     deterministic verifier, so a failed judge call never publishes a finding.
/// </summary>
/// <remarks>
///     When a protocol recorder and <see cref="ReviewVerificationContext.ProtocolId" /> are available, each judge call is
///     recorded as an AI call with its token usage, and its tokens are added to the protocol with the judging model.
///     The category is the low-effort category unless the host passes another one: a runner resolves every purpose to
///     the manifest's default model and records the judge under the default category. When the judge falls back to the
///     reviewer's own client, its tokens are recorded under the default category. Claims are judged a few at a time,
///     each judge call is limited in output tokens and in time, and a call that runs out of time withholds its claim.
///     A failure of the model provider, an error in its response, or a response without output and token usage
///     withholds the claim with the provider-error degradation. Whether the producing pass could read the repository is
///     taken from the work item when it states it, and from the verification context otherwise.
/// </remarks>
public sealed class EvidenceBackedReviewVerifier(
    IProtocolRecorder? protocolRecorder = null,
    ILogger<EvidenceBackedReviewVerifier>? logger = null,
    AiConnectionModelCategory resolvedJudgeTokenCategory = AiConnectionModelCategory.LowEffort,
    TimeSpan? judgeCallTimeout = null,
    TimeSpan? excerptTimeout = null,
    TimeSpan? recordingGrace = null) : IReviewFindingVerifier
{
    private const string JudgeCallName = "ai_call_evidence_verification";

    // An intermediate model call of the tool loop, in which the judge asked for lookups.
    private const string JudgeToolTurnCallName = "ai_call_evidence_verification_tool_turn";

    /// <summary>
    ///     Output limit of one judge call. The judge answers with a one-sentence JSON verdict, which needs far fewer
    ///     tokens; the limit stops a reasoning model from spending an unbounded amount on one finding.
    /// </summary>
    internal const int MaxJudgeOutputTokens = 400;

    /// <summary>Number of judge calls that run at the same time for one file.</summary>
    internal const int MaxParallelJudgeCalls = 4;

    private const int MaxAnchorChars = 8000;
    private const int MaxAnchorLines = 400;
    private const string SystemPromptStageKey = "evidence_verification_system";
    private const string UserPromptStageKey = "evidence_verification_user";

    /// <summary>
    ///     Time limit of one claim: the anchor read, the excerpt reads and the whole tool loop of the judge together.
    /// </summary>
    private static readonly TimeSpan DefaultJudgeCallTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Time limit of the excerpt reads of one claim, inside the claim's time limit.</summary>
    private static readonly TimeSpan DefaultExcerptTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    ///     Time after the claim's time limit during which a judge call still waits to be recorded. Recording is
    ///     serialized, so a slow write for another claim would otherwise keep this claim waiting without limit.
    /// </summary>
    private static readonly TimeSpan DefaultRecordingGrace = TimeSpan.FromSeconds(10);

    private static readonly HashSet<string> PlaceholderSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "n/a", "na", "null", "-", "unknown", "nothing", "not applicable",
    };

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private readonly TimeSpan _judgeCallTimeout = judgeCallTimeout ?? DefaultJudgeCallTimeout;
    private readonly TimeSpan _excerptTimeout = excerptTimeout ?? DefaultExcerptTimeout;
    private readonly TimeSpan _recordingGrace = recordingGrace ?? DefaultRecordingGrace;

    public async Task<IReadOnlyList<VerificationOutcome>> VerifyAsync(
        IReadOnlyList<VerificationWorkItem> workItems,
        IReadOnlyList<InvariantFact> invariantFacts,
        ReviewVerificationContext? verificationContext = null,
        CancellationToken ct = default)
    {
        _ = invariantFacts;
        ArgumentNullException.ThrowIfNull(workItems);

        // Resolve the judging runtime once for the whole work-item set: the low-effort model, or the reviewer's own tier
        // client when no resolver is available or resolving the low-effort purpose fails, for example because no model
        // is bound to it.
        var judge = await this.ResolveJudgeRuntimeAsync(verificationContext, ct).ConfigureAwait(false);

        // The claims are judged a few at a time. Each outcome is stored at the index of its work item, so the order of
        // the result follows the order of the input whatever order the calls finish in. Recording into the protocol is
        // serialized; see RecordSerializedAsync.
        var outcomes = new VerificationOutcome[workItems.Count];
        using var reads = new SourceReadCache(verificationContext, ct);
        using var callSlots = new SemaphoreSlim(MaxParallelJudgeCalls);
        using var recordingLock = new SemaphoreSlim(1);
        var calls = workItems.Select(async (workItem, index) =>
        {
            await callSlots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                outcomes[index] = await this.VerifyOneAsync(workItem, verificationContext, judge, reads, recordingLock, ct).ConfigureAwait(false);
            }
            finally
            {
                callSlots.Release();
            }
        });
        await Task.WhenAll(calls).ConfigureAwait(false);

        // An excerpt read that outlived its claim's limit is still running; it is cancelled once every claim is decided.
        reads.CancelPendingReads();
        return outcomes;
    }

    private async Task<JudgeRuntime> ResolveJudgeRuntimeAsync(
        ReviewVerificationContext? context,
        CancellationToken ct)
    {
        if (context?.Resolver is not null && context.ClientId != Guid.Empty)
        {
            try
            {
                var runtime = await context.Resolver
                    .ResolveChatRuntimeAsync(context.ClientId, AiPurpose.ReviewLowEffort, ct)
                    .ConfigureAwait(false);
                return new JudgeRuntime(runtime.ChatClient, runtime.Model.RemoteModelId, resolvedJudgeTokenCategory);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await this.RecordResolutionFallbackAsync(context, ex, ct).ConfigureAwait(false);
            }
        }

        return new JudgeRuntime(context?.ChatClient, context?.ModelId, AiConnectionModelCategory.Default);
    }

    // The low-effort model could not be resolved, so the judge runs on the reviewer's own tier client. The fallback is
    // logged and recorded in the protocol with the model actually used, because that model can be far more expensive.
    private async Task RecordResolutionFallbackAsync(ReviewVerificationContext context, Exception ex, CancellationToken ct)
    {
        this._logger.LogWarning(
            ex,
            "The low-effort judge model could not be resolved for client {ClientId}; the evidence judge falls back to the reviewer model {ModelId}",
            context.ClientId,
            context.ModelId ?? "(none)");

        if (protocolRecorder is null || !context.ProtocolId.HasValue)
        {
            return;
        }

        try
        {
            await protocolRecorder.RecordVerificationEventAsync(
                context.ProtocolId.Value,
                ReviewProtocolEventNames.VerificationDegraded,
                JsonSerializer.Serialize(
                    new
                    {
                        stage = ClaimDescriptor.LocalStage,
                        degradedComponent = "judge_model_resolution",
                        requestedPurpose = nameof(AiPurpose.ReviewLowEffort),
                        fallbackModelId = context.ModelId,
                    }),
                null,
                Truncate(ex.Message, 500),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // The trace entry is lost; the log line above still states the fallback.
        }
    }

    private async Task<VerificationOutcome> VerifyOneAsync(
        VerificationWorkItem workItem,
        ReviewVerificationContext? context,
        JudgeRuntime judge,
        SourceReadCache reads,
        SemaphoreSlim recordingLock,
        CancellationToken ct)
    {
        var claim = workItem.Claim;

        if (judge.Client is null || context?.Tools is null)
        {
            return ConservativeWithhold(
                claim,
                "escalation skipped: no judge client or review tools available",
                degradation: EvidenceJudgeDegradations.Unavailable);
        }

        var producedWithRepositoryTools = workItem.ProducedWithRepositoryTools ?? context.PassReviewedWithRepositoryTools;
        var recording = new ClaimRecording(recordingLock, DateTimeOffset.UtcNow + this._judgeCallTimeout + this._recordingGrace, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(this._judgeCallTimeout);
        try
        {
            if (string.IsNullOrWhiteSpace(claim.AnchorFilePath))
            {
                // A claim without an anchor file, such as a cross-cutting finding, is judged from the pull request
                // inputs, excerpts of the files the finding cites, and the judge's own lookups.
                var supportingExcerpts = await this.BuildSupportingFileExcerptsAsync(claim, workItem, reads, timeout.Token).ConfigureAwait(false);
                return await this.JudgeAsync(claim, context, judge, recording, string.Empty, 1, supportingExcerpts, producedWithRepositoryTools, timeout.Token)
                    .ConfigureAwait(false);
            }

            var (windowStart, windowEnd) = ComputeAnchorWindow(claim.AnchorLineNumber);
            var anchorSource = await context.Tools
                .GetFileContentAsync(claim.AnchorFilePath!, context.SourceBranch, windowStart, windowEnd, timeout.Token)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(anchorSource))
            {
                return ConservativeWithhold(
                    claim,
                    $"escalation degraded: anchor source empty for {claim.AnchorFilePath}",
                    degradation: EvidenceJudgeDegradations.AnchorUnreadable);
            }

            var (boundedSource, boundedStartLine) = BoundAnchorChars(anchorSource, windowStart, claim.AnchorLineNumber);

            // The judge also receives bounded excerpts of what the reviewing pass read and of the anchor file outside
            // the window, for the symbols the claim names, so a claim whose evidence lies elsewhere can be decided.
            var shownEndLine = boundedStartLine + CountSourceLines(boundedSource) - 1;
            var windowCoversFile = windowStart == 1
                                   && CountSourceLines(anchorSource) < MaxAnchorLines
                                   && ReferenceEquals(boundedSource, anchorSource);
            var excerpts = await this.BuildExcerptsAsync(claim, context, reads, boundedStartLine, shownEndLine, windowCoversFile, timeout.Token)
                .ConfigureAwait(false);

            return await this.JudgeAsync(
                    claim, context, judge, recording, boundedSource, boundedStartLine, excerpts, producedWithRepositoryTools, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return ConservativeWithhold(
                claim,
                $"escalation degraded: the judge did not answer within {this._judgeCallTimeout.TotalSeconds:0} seconds",
                degradation: EvidenceJudgeDegradations.Timeout);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (EvidenceJudgeProviderException ex)
        {
            return ConservativeWithhold(
                claim,
                $"escalation degraded: the judge model call failed: {Truncate(ex.Message, 200)}",
                degradation: EvidenceJudgeDegradations.ProviderError);
        }
        catch (Exception ex)
        {
            // Any failure keeps the conservative withhold, and the cause is carried in the outcome so the recorded local
            // decision shows what failed.
            return ConservativeWithhold(
                claim,
                $"escalation degraded: {ex.GetType().Name}: {Truncate(ex.Message, 160)}",
                degradation: EvidenceJudgeDegradations.Error);
        }
    }

    // Runs the bounded tool loop for one claim and maps the final answer onto the local decision. Every model call and
    // every tool call is recorded under the protocol of the pass, so the trace shows what the judge looked at.
    private async Task<VerificationOutcome> JudgeAsync(
        ClaimDescriptor claim,
        ReviewVerificationContext context,
        JudgeRuntime judge,
        ClaimRecording recording,
        string anchorSource,
        int sourceStartLine,
        IReadOnlyList<JudgeExcerpt> excerpts,
        bool producedWithRepositoryTools,
        CancellationToken claimToken)
    {
        var systemPrompt = BuildSystemPrompt();
        var userMessage = BuildUserMessage(claim, anchorSource, sourceStartLine, context.Intent, excerpts, context.SourceBranch);
        var response = await EvidenceJudgeToolLoop.RunAsync(
            judge.Client!,
            new ChatOptions { ModelId = judge.ModelId, MaxOutputTokens = MaxJudgeOutputTokens },
            systemPrompt,
            userMessage,
            EvidenceJudgeToolLoop.BuildJudgeTools(context.Tools!, claimToken),
            (modelResponse, isFinal) => this.RecordSerializedAsync(
                recording,
                claim,
                recordToken => this.RecordJudgeCallAsync(
                    context.ProtocolId, modelResponse, systemPrompt, userMessage, judge, isFinal ? JudgeCallName : JudgeToolTurnCallName, recordToken)),
            toolCall => this.RecordSerializedAsync(
                recording, claim, recordToken => this.RecordJudgeToolCallAsync(context.ProtocolId, claim, toolCall, recordToken)),
            claimToken).ConfigureAwait(false);

        // A provider error withholds the claim even when the response also carries a parseable verdict, because the
        // response may be incomplete.
        var verdict = TryParseVerdict(response.Text);
        if ((verdict is null || ProviderError(response) is not null) && UnusableResponseOutcome(claim, response) is { } unusable)
        {
            return unusable;
        }

        return ToOutcome(claim, verdict, producedWithRepositoryTools);
    }

    // Recording is serialized, because the protocol recorder adds token counts by reading and rewriting the protocol
    // row. The wait for the lock ends a short time after the claim's limit, and the record itself gets at least the
    // recording grace after it takes the lock, so a call is still recorded after its claim ran out of time. A record
    // that cannot take the lock or does not finish in time is skipped and logged, so a stalled recording cannot hold
    // this claim or the claims waiting behind it open.
    private async Task RecordSerializedAsync(ClaimRecording recording, ClaimDescriptor claim, Func<CancellationToken, Task> record)
    {
        var remaining = recording.LockDeadline - DateTimeOffset.UtcNow;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(recording.RunToken);
        wait.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        try
        {
            await recording.Lock.WaitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!recording.RunToken.IsCancellationRequested)
        {
            this._logger.LogWarning(
                "A judge call for claim {ClaimId} was not recorded, because the protocol recording did not become available in time",
                claim.ClaimId);
            return;
        }

        try
        {
            var writeTime = recording.LockDeadline - DateTimeOffset.UtcNow;
            using var write = CancellationTokenSource.CreateLinkedTokenSource(recording.RunToken);
            write.CancelAfter(writeTime > this._recordingGrace ? writeTime : this._recordingGrace);
            await record(write.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!recording.RunToken.IsCancellationRequested)
        {
            this._logger.LogWarning(
                "A judge call for claim {ClaimId} was not recorded completely, because the protocol recording did not finish in time",
                claim.ClaimId);
        }
        finally
        {
            recording.Lock.Release();
        }
    }

    // Classifies a final response that holds no verdict: a provider error or an empty response is a provider failure,
    // and a lookup requested in the final turn, where the judge may no longer call tools, is an unusable answer.
    // Returns null when the response is an ordinary answer that the verdict parser rejected.
    // A refusal arrives as error content as well, but it is the model's answer, not a provider failure.
    private static ErrorContent? ProviderError(ChatResponse response)
    {
        return response.Messages
            .SelectMany(message => message.Contents)
            .OfType<ErrorContent>()
            .FirstOrDefault(content => !string.Equals(content.ErrorCode, "Refusal", StringComparison.Ordinal));
    }

    private static VerificationOutcome? UnusableResponseOutcome(ClaimDescriptor claim, ChatResponse response)
    {
        var contents = response.Messages.SelectMany(message => message.Contents).ToList();

        var error = ProviderError(response);
        if (error is not null)
        {
            var code = string.IsNullOrWhiteSpace(error.ErrorCode) ? string.Empty : $"{error.ErrorCode}: ";
            return ConservativeWithhold(
                claim,
                $"escalation degraded: the provider returned an error for the judge call: {Truncate(code + error.Message, 200)}",
                degradation: EvidenceJudgeDegradations.ProviderError);
        }

        if (string.IsNullOrWhiteSpace(response.Text) && contents.OfType<FunctionCallContent>().Any())
        {
            return ConservativeWithhold(
                claim,
                "escalation degraded: the judge asked for a lookup in its final turn instead of answering with a verdict",
                EvidenceJudgeVerdicts.Unparseable,
                EvidenceJudgeDegradations.Unparseable);
        }

        // A streamed response whose provider reported a failure, and an exhausted subscription quota, both arrive as a
        // response without any output and without token usage.
        var hasOutput = contents.Any(content => content is not UsageContent && content is not TextContent { Text.Length: 0 });
        var hasUsage = response.Usage is { } usage && (usage.InputTokenCount > 0 || usage.OutputTokenCount > 0);
        if (!hasOutput && !hasUsage)
        {
            return ConservativeWithhold(
                claim,
                "escalation degraded: the provider returned an empty response for the judge call, without output or token usage",
                degradation: EvidenceJudgeDegradations.ProviderError);
        }

        return null;
    }

    private async Task RecordJudgeToolCallAsync(Guid? protocolId, ClaimDescriptor claim, JudgeToolCall toolCall, CancellationToken ct)
    {
        if (protocolRecorder is null || !protocolId.HasValue)
        {
            return;
        }

        try
        {
            await protocolRecorder.RecordVerificationEventAsync(
                protocolId.Value,
                ReviewProtocolEventNames.EvidenceJudgeToolCall,
                JsonSerializer.Serialize(new { findingId = claim.FindingId, claimId = claim.ClaimId, tool = toolCall.ToolName }),
                JsonSerializer.Serialize(
                    new
                    {
                        tool = toolCall.ToolName,
                        arguments = toolCall.Arguments,
                        resultChars = toolCall.ResultChars,
                        resultTruncated = toolCall.ResultTruncated,
                    }),
                null,
                ct).ConfigureAwait(false);
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

    // Records the judge call and adds its tokens to the protocol, as the PR-level verifier does for its calls. A recording
    // failure does not change the verification outcome.
    private async Task RecordJudgeCallAsync(
        Guid? protocolId,
        ChatResponse response,
        string systemPrompt,
        string userMessage,
        JudgeRuntime judge,
        string callName,
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
                ResponseSummary(response),
                ct,
                callName,
                cachedInputTokens: usage.IsEstimated ? null : usage.CachedInputTokens,
                cacheWriteTokens: usage.IsEstimated ? null : usage.CacheWriteTokens,
                reasoningTokens: usage.IsEstimated ? null : usage.ReasoningTokens).ConfigureAwait(false);
            await protocolRecorder.AddTokensAsync(
                protocolId.Value,
                usage.InputTokens,
                usage.OutputTokens,
                judge.Category,
                judge.ModelId,
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
    // source in its summary. A finding the judge cannot decide because the code it needs is not in the inputs is
    // published when the producing pass could read the repository, and withheld otherwise. Every other response keeps
    // the conservative withhold, so an unusable verdict never publishes a finding.
    private static VerificationOutcome ToOutcome(ClaimDescriptor claim, ParsedVerdict? verdict, bool passReviewedWithRepositoryTools)
    {
        if (verdict is null)
        {
            return ConservativeWithhold(
                claim, "escalation degraded: judge response was not a parseable verdict", EvidenceJudgeVerdicts.Unparseable,
                EvidenceJudgeDegradations.Unparseable);
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
                    claim,
                    "escalation degraded: the contradiction verdict named no contradicted source",
                    EvidenceJudgeVerdicts.Unparseable,
                    EvidenceJudgeDegradations.Unparseable);
            case EvidenceJudgeVerdicts.Intended:
                return ConservativeWithhold(
                    claim, $"judge found the behaviour intended and contradicting nothing: {Truncate(verdict.Reason, 200)}", EvidenceJudgeVerdicts.Intended);
            case EvidenceJudgeVerdicts.NotConfirmed:
                return ConservativeWithhold(claim, $"judge did not confirm: {Truncate(verdict.Reason, 200)}", EvidenceJudgeVerdicts.NotConfirmed);
            case EvidenceJudgeVerdicts.NotActionable:
                return ConservativeWithhold(
                    claim,
                    $"judge found the finding true but not actionable: {Truncate(verdict.Reason, 200)}",
                    EvidenceJudgeVerdicts.NotActionable);
            case EvidenceJudgeVerdicts.InsufficientContext when passReviewedWithRepositoryTools:
                return PublishUndecided(claim, Truncate(verdict.Reason, 200));
            case EvidenceJudgeVerdicts.InsufficientContext:
                return ConservativeWithhold(
                    claim,
                    $"judge lacked the code to decide, and the pass that produced the finding had no repository tools: {Truncate(verdict.Reason, 200)}",
                    EvidenceJudgeVerdicts.InsufficientContext);
            default:
                return ConservativeWithhold(
                    claim,
                    $"escalation degraded: judge returned the unknown verdict '{Truncate(verdict.Kind, 40)}'",
                    EvidenceJudgeVerdicts.Unparseable,
                    EvidenceJudgeDegradations.Unparseable);
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

    // A finding the judge could not decide from the code it received, produced by a pass that could read the
    // repository. It is published with weak evidence, so the trace separates it from a confirmed finding.
    private static VerificationOutcome PublishUndecided(ClaimDescriptor claim, string reason)
    {
        return new VerificationOutcome(
            claim.ClaimId,
            claim.FindingId,
            VerificationOutcome.InsufficientEvidenceKind,
            FinalGateDecision.PublishDisposition,
            [ReviewFindingGateReasonCodes.DefaultPublish],
            [],
            VerificationOutcome.WeakEvidence,
            $"Published undecided: the judge lacked the code to decide, and the pass that produced the finding could read the repository ({reason}).",
            VerificationOutcome.AiMicroVerifierEvaluator,
            false)
        {
            JudgeVerdict = EvidenceJudgeVerdicts.InsufficientContext,
        };
    }

    private static VerificationOutcome ConservativeWithhold(
        ClaimDescriptor claim,
        string? cause = null,
        string? judgeVerdict = null,
        string? degradation = null)
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
            JudgeDegradation = degradation,
        };
    }

    private static string BuildSystemPrompt()
    {
        return PromptTemplateRuntime.RenderStage(SystemPromptStageKey);
    }

    private static string BuildUserMessage(
        ClaimDescriptor claim,
        string anchorSource,
        int sourceStartLine,
        ReviewVerificationIntent? intent,
        IReadOnlyList<JudgeExcerpt> excerpts,
        string sourceBranch)
    {
        return PromptTemplateRuntime.RenderStage(
            UserPromptStageKey,
            EvidenceJudgeInput.BuildUserModel(claim, anchorSource, sourceStartLine, intent, excerpts, sourceBranch));
    }

    // The recorded output of a model call: its text, or the lookups it asked for when it has no text.
    private static string ResponseSummary(ChatResponse response)
    {
        if (!string.IsNullOrWhiteSpace(response.Text))
        {
            return response.Text;
        }

        var calls = response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Select(call => call.Name).ToList();
        return calls.Count == 0 ? string.Empty : JsonSerializer.Serialize(new { toolCalls = calls });
    }

    // Number of source lines in a bounded window, without the truncation marker lines and a final line break.
    private static int CountSourceLines(string source)
    {
        return Math.Max(
            1,
            source.TrimEnd('\n').Split('\n').Count(line => !string.Equals(line, EvidenceJudgeInput.TruncationMarker, StringComparison.Ordinal)));
    }

    // The excerpts are optional input. They are built under their own time limit inside the claim's limit, so a slow
    // read costs the excerpts and leaves the judge call its time.
    private async Task<IReadOnlyList<JudgeExcerpt>> BuildExcerptsAsync(
        ClaimDescriptor claim,
        ReviewVerificationContext context,
        SourceReadCache reads,
        int shownStartLine,
        int shownEndLine,
        bool windowCoversFile,
        CancellationToken claimToken)
    {
        using var excerptTimeout = CancellationTokenSource.CreateLinkedTokenSource(claimToken);
        excerptTimeout.CancelAfter(this._excerptTimeout);
        try
        {
            return await EvidenceJudgeReviewerExcerpts.BuildAsync(
                claim,
                context.ReviewerReads ?? [],
                shownStartLine,
                shownEndLine,
                windowCoversFile,
                (path, startLine, endLine, token) => reads.ReadAsync(path, startLine, endLine, token),
                excerptTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (excerptTimeout.IsCancellationRequested && !claimToken.IsCancellationRequested)
        {
            this._logger.LogWarning(
                "Reading the reviewer excerpts for {FilePath} took longer than {Seconds} seconds; the judge decides without them",
                claim.AnchorFilePath,
                this._excerptTimeout.TotalSeconds);
            return [];
        }
    }

    // Excerpts of the files a finding without an anchor file cites, which the PR-level verification fetched as its
    // evidence. They are built under the excerpt time limit, like the reviewer excerpts.
    private async Task<IReadOnlyList<JudgeExcerpt>> BuildSupportingFileExcerptsAsync(
        ClaimDescriptor claim,
        VerificationWorkItem workItem,
        SourceReadCache reads,
        CancellationToken claimToken)
    {
        var supportingFiles = workItem.ExistingEvidence?.SupportingFiles ?? [];
        if (supportingFiles.Count == 0)
        {
            return [];
        }

        using var excerptTimeout = CancellationTokenSource.CreateLinkedTokenSource(claimToken);
        excerptTimeout.CancelAfter(this._excerptTimeout);
        try
        {
            return await EvidenceJudgeReviewerExcerpts.BuildSupportingFileExcerptsAsync(
                claim,
                supportingFiles,
                (path, startLine, endLine, token) => reads.ReadAsync(path, startLine, endLine, token),
                excerptTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (excerptTimeout.IsCancellationRequested && !claimToken.IsCancellationRequested)
        {
            this._logger.LogWarning(
                "Reading the files cited by claim {ClaimId} took longer than {Seconds} seconds; the judge decides without them",
                claim.ClaimId,
                this._excerptTimeout.TotalSeconds);
            return [];
        }
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

    // Reads each file range once per verification run, so the claims of one file share the reads of the excerpts. A
    // read runs under a token scoped to the run, which is cancelled when the run ends; each claim waits for the read
    // under its own time limit.
    private sealed class SourceReadCache(ReviewVerificationContext? context, CancellationToken callerToken) : IDisposable
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, int Start, int End), Lazy<Task<string>>> _reads = new();
        private readonly CancellationTokenSource _run = CancellationTokenSource.CreateLinkedTokenSource(callerToken);

        public void CancelPendingReads()
        {
            this._run.Cancel();
        }

        public void Dispose()
        {
            this._run.Dispose();
        }

        public Task<string> ReadAsync(string path, int startLine, int endLine, CancellationToken ct)
        {
            if (context?.Tools is null)
            {
                return Task.FromResult(string.Empty);
            }

            var read = this._reads.GetOrAdd((path, startLine, endLine), key => new Lazy<Task<string>>(() => this.ReadOnceAsync(context.Tools, key)));
            return read.Value.WaitAsync(ct);
        }

        // A failed or cancelled read is removed from the cache, so a later claim that needs the same range reads it again.
        private async Task<string> ReadOnceAsync(IReviewContextTools tools, (string Path, int Start, int End) key)
        {
            try
            {
                return await tools.GetFileContentAsync(key.Path, context!.SourceBranch, key.Start, key.End, this._run.Token).ConfigureAwait(false);
            }
            catch
            {
                this._reads.TryRemove(key, out _);
                throw;
            }
        }
    }

    private sealed record JudgeRuntime(IChatClient? Client, string? ModelId, AiConnectionModelCategory Category);

    // The serialized protocol recording of one claim: the lock shared by the run, the time after which the claim no
    // longer waits for that lock, and the run token the records themselves use.
    private sealed record ClaimRecording(SemaphoreSlim Lock, DateTimeOffset LockDeadline, CancellationToken RunToken);
}
