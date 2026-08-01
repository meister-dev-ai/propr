// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

/// <summary>
///     Model-backed per-file complexity classifier. Resolves a configured cheap model via
///     <see cref="AiPurpose.ReviewTriage" /> and asks it to judge the diff's complexity and likely impact;
///     falls back to the deterministic size heuristic (<see cref="ReviewDiffProcessor.ClassifyTier" />)
///     when the binding is missing or the call fails or returns an unparseable response. Never throws.
/// </summary>
/// <remarks>
///     What it spends is reported like any other model call. Triage runs once per file and is cheap per call,
///     but it was the one caller that recorded nothing at all, so its tokens were billed by the provider and
///     counted by no one. That is invisible spend rather than free spend, and it grows with the file count.
/// </remarks>
internal sealed partial class ReviewTriageClassifier(
    IAiRuntimeResolver aiRuntimeResolver,
    ILogger<ReviewTriageClassifier> logger,
    IModelUsageRecorder? usageRecorder = null) : IReviewComplexityClassifier, IDisposable
{
    private const int MaxDiffChars = 6000;
    private const int MaxOtherPaths = 40;

    private readonly SemaphoreSlim _resolutionGate = new(1, 1);
    private IResolvedAiChatRuntime? _resolvedRuntime;
    private Guid _resolvedClientId;

    public void Dispose()
    {
        this._resolutionGate.Dispose();
    }

    public async Task<TriageVerdict> ClassifyAsync(
        Guid clientId,
        ChangedFile file,
        FanOutSignal fanOut,
        IReadOnlyList<string> changedFilePaths,
        CancellationToken ct)
    {
        var fallbackTier = ReviewDiffProcessor.ClassifyTier(file);

        IResolvedAiChatRuntime runtime;
        try
        {
            runtime = await this.ResolveRuntimeAsync(clientId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AiPurposeBindingNotConfiguredException ex)
        {
            // The expected case: this client does not use a triage model, so the deterministic heuristic tiers the file.
            LogTriageBindingUnavailable(logger, file.Path, ex);
            return new TriageVerdict(fallbackTier, false, "size-heuristic fallback: no ReviewTriage binding");
        }
        catch (Exception ex)
        {
            // Anything else is a fault rather than a configuration choice. The review still proceeds on the heuristic,
            // because losing it over triage would be worse, but it must not be reported as an absent binding: doing so
            // sent operators to check mappings that were correct while the real cause went unnamed.
            LogTriageResolutionFailed(logger, file.Path, ex);
            return new TriageVerdict(fallbackTier, false, "size-heuristic fallback: triage runtime could not be resolved");
        }

        try
        {
            var response = await runtime.ChatClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, BuildSystemPrompt()),
                    new ChatMessage(ChatRole.User, BuildUserMessage(file, fanOut, changedFilePaths)),
                ],
                new ChatOptions(),
                ct).ConfigureAwait(false);

            // Recorded before the verdict is read, because the tokens are spent whether or not the answer turns
            // out to be usable. Reporting only the calls that parsed would understate what triage actually costs.
            // The recorder writes the daily per-client usage row, so it is absent exactly when no database is
            // configured, and there is then nowhere for the number to go.
            if (usageRecorder is not null)
            {
                await usageRecorder.RecordAsync(clientId, runtime, response, ct).ConfigureAwait(false);
            }

            // Carried on the verdict so the caller can bill it to the file's protocol once one exists. Triage
            // decides which model the review will use, so it necessarily runs before there is anything to bill.
            var spend = MeasureSpend(runtime, response);

            return (TryParse(response.Text, fallbackTier)
                    ?? new TriageVerdict(fallbackTier, false, "size-heuristic fallback: unparseable triage response"))
                with
                {
                    Spend = spend
                };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogTriageCallFailed(logger, file.Path, ex);
            return new TriageVerdict(fallbackTier, false, "size-heuristic fallback: triage call failed");
        }
    }

    /// <summary>
    ///     Resolves the triage runtime once and shares it across the files being classified.
    /// </summary>
    /// <remarks>
    ///     Files are reviewed concurrently, and resolution reads the logical-model catalog, the connection, and the
    ///     client through repositories that share one scoped <c>DbContext</c>. Letting those reads overlap threw
    ///     "a second operation was started on this context instance", which this class caught and reported as a
    ///     fault, so every file silently fell back to the size heuristic and model-judged triage never ran at all.
    ///     Serializing the resolution removes the overlap, and caching the result means a job pays for it once
    ///     rather than once per file. The cache is keyed by client because the interface permits a caller to vary
    ///     it, and lives only as long as this scoped instance, so configuration changes are picked up by the next
    ///     job rather than being pinned for the process.
    /// </remarks>
    private async Task<IResolvedAiChatRuntime> ResolveRuntimeAsync(Guid clientId, CancellationToken ct)
    {
        await this._resolutionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (this._resolvedRuntime is not null && this._resolvedClientId == clientId)
            {
                return this._resolvedRuntime;
            }

            // A failure is deliberately not cached: the caller treats it as a per-file fallback and logs it as one,
            // and a transient fault must not disable triage for the rest of the job.
            var runtime = await aiRuntimeResolver.ResolveChatRuntimeAsync(clientId, AiPurpose.ReviewTriage, ct).ConfigureAwait(false);
            this._resolvedRuntime = runtime;
            this._resolvedClientId = clientId;
            return runtime;
        }
        finally
        {
            this._resolutionGate.Release();
        }
    }

    /// <summary>Reads what the call consumed, in the terms the job breakdown records.</summary>
    private static TriageSpend? MeasureSpend(IResolvedAiChatRuntime runtime, ChatResponse response)
    {
        var usage = AiTokenUsageExtractor.FromResponse(response, runtime.Connection.ProviderKind);

        // A response with no usage payload extracts as all-zero. Attributing that would add a line to the
        // breakdown claiming the call was free, which is a stronger statement than "the provider did not say".
        if (usage.InputTokens <= 0 && usage.OutputTokens <= 0)
        {
            return null;
        }

        return new TriageSpend(
            runtime.Model.RemoteModelId,
            runtime.LogicalModelName,
            usage.InputTokens,
            usage.OutputTokens,
            usage.CachedInputTokens,
            usage.CacheWriteTokens,
            usage.ReasoningTokens);
    }

    private static string BuildSystemPrompt()
    {
        return PromptTemplateRuntime.RenderStage("triage_system");
    }

    private static string BuildUserMessage(ChangedFile file, FanOutSignal fanOut, IReadOnlyList<string> changedFilePaths)
    {
        var diff = file.UnifiedDiff.Length > MaxDiffChars
            ? string.Concat(file.UnifiedDiff.AsSpan(0, MaxDiffChars), "\n…(diff truncated)")
            : file.UnifiedDiff;

        var fanOutText = fanOut.Kind switch
        {
            FanOutKind.Measured => $"{fanOut.Count} confirmed reference(s) to the changed symbols",
            FanOutKind.Truncated => $"at least {fanOut.Count} references (too many to count — high blast radius)",
            _ => "unknown (no structural data)",
        };

        var otherPaths = changedFilePaths
            .Where(p => !string.Equals(p, file.Path, StringComparison.Ordinal))
            .Take(MaxOtherPaths)
            .ToList();
        var scope = otherPaths.Count == 0
            ? "this is the only changed file"
            : "other changed files in this PR: " + string.Join(", ", otherPaths);

        return PromptTemplateRuntime.RenderStage(
            "triage_user",
            new PromptTemplateModels.TriageUserModel(file.Path, fanOutText, scope, diff));
    }

    private static TriageVerdict? TryParse(string? responseText, FileComplexityTier fallbackTier)
    {
        var json = ExtractJsonObject(responseText);
        if (json is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var tier = root.TryGetProperty("tier", out var tierEl) && tierEl.ValueKind == JsonValueKind.String
                ? MapTier(tierEl.GetString(), fallbackTier)
                : fallbackTier;

            var securityEscalate = root.TryGetProperty("securityEscalate", out var secEl)
                                   && secEl.ValueKind == JsonValueKind.True;

            var why = root.TryGetProperty("why", out var whyEl) && whyEl.ValueKind == JsonValueKind.String
                ? whyEl.GetString() ?? string.Empty
                : string.Empty;

            return new TriageVerdict(tier, securityEscalate, Truncate(why, 200));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static FileComplexityTier MapTier(string? value, FileComplexityTier fallback)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "low" => FileComplexityTier.Low,
            "medium" => FileComplexityTier.Medium,
            "high" => FileComplexityTier.High,
            _ => fallback,
        };
    }

    private static string? ExtractJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private static string Truncate(string value, int max)
    {
        return value.Length <= max ? value : value[..max];
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "ReviewTriage binding unavailable for {Path}; using size heuristic.")]
    private static partial void LogTriageBindingUnavailable(ILogger logger, string path, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Resolving the ReviewTriage runtime failed for {Path}; using size heuristic. This is a fault, not a missing binding.")]
    private static partial void LogTriageResolutionFailed(ILogger logger, string path, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ReviewTriage call failed for {Path}; using size heuristic.")]
    private static partial void LogTriageCallFailed(ILogger logger, string path, Exception ex);
}
