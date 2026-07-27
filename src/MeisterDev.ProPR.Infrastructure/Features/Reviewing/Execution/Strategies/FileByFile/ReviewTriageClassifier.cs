// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

/// <summary>
///     Model-backed per-file complexity classifier. Resolves a configured cheap model via
///     <see cref="AiPurpose.ReviewTriage" /> and asks it to judge the diff's complexity and likely impact;
///     falls back to the deterministic size heuristic (<see cref="ReviewDiffProcessor.ClassifyTier" />)
///     when the binding is missing or the call fails or returns an unparseable response. Never throws.
/// </summary>
internal sealed partial class ReviewTriageClassifier(
    IAiRuntimeResolver aiRuntimeResolver,
    ILogger<ReviewTriageClassifier> logger) : IReviewComplexityClassifier
{
    private const int MaxDiffChars = 6000;
    private const int MaxOtherPaths = 40;

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
            runtime = await aiRuntimeResolver.ResolveChatRuntimeAsync(clientId, AiPurpose.ReviewTriage, ct).ConfigureAwait(false);
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

            return TryParse(response.Text, fallbackTier)
                   ?? new TriageVerdict(fallbackTier, false, "size-heuristic fallback: unparseable triage response");
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

    private static string BuildSystemPrompt()
    {
        return "You are a code-review triage classifier. Judge how much review effort a single file's diff "
               + "deserves based on its complexity and likely impact — NOT merely its size (a tiny change can be "
               + "high-impact). Respond with ONLY a JSON object and nothing else: "
               + "{\"tier\":\"low|medium|high\",\"securityEscalate\":true|false,\"why\":\"short reason\"}. "
               + "Use tier=low for trivial/mechanical changes, medium for ordinary logic, high for complex or "
               + "wide-impact changes. Set securityEscalate=true only if the diff plausibly affects authentication, "
               + "authorization, secrets, cryptography, input validation, or similar security-sensitive behavior.";
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

        return $"File: {file.Path}\nBlast radius: {fanOutText}\nPR scope: {scope}\n\nDiff:\n{diff}";
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
