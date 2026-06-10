// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Features.Reviewing.Execution.Services;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.ProRV.Abstractions;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

internal sealed class FileByFileContextPrefetchStage(AiReviewOptions? options = null, IProtocolRecorder? protocolRecorder = null)
    : IReviewPipelineStage<PerFileReviewContext>
{
    public const string StageIdConstant = "file-by-file.context-prefetch";

    private readonly AiReviewOptions _options = options ?? new AiReviewOptions();
    private readonly IProtocolRecorder? _protocolRecorder = protocolRecorder;

    public string StageId => StageIdConstant;

    public async Task<PerFileReviewContext> ExecuteAsync(PerFileReviewContext context, CancellationToken cancellationToken)
    {
        var fileHint = context.FileReviewContext.PerFileHint;
        var reviewTools = context.FileReviewContext.ReviewTools;
        if (fileHint is null || reviewTools is null)
        {
            return context;
        }

        var evidence = new List<PrefetchedContextEvidenceItem>();
        var (surroundingContent, surroundingTruncated, windowedInjection, windowCount, firstWindowStartLine) =
            BuildSurroundingContext(
                context.ChangedFile.FullContent,
                context.ChangedFile.UnifiedDiff,
                this._options);

        if (!string.IsNullOrWhiteSpace(surroundingContent))
        {
            evidence.Add(
                new PrefetchedContextEvidenceItem(
                    "surrounding_definition",
                    "Changed definition context",
                    context.ChangedFile.Path,
                    surroundingContent,
                    surroundingTruncated));
        }

        var callerBudget = Math.Max(0, this._options.MaxPrefetchCallerSites);
        var searchResult = callerBudget == 0
            ? null
            : await reviewTools.SearchCodeAsync(
                new CodeSearchRequest(
                    context.ChangedFile.Path,
                    CodeSearchModes.RelatedSymbol,
                    RepositorySearchBranchSides.Source,
                    RepositorySearchPathScopes.Repository),
                cancellationToken);

        if (searchResult is { Status: RepositorySearchStatuses.Success or RepositorySearchStatuses.Partial })
        {
            foreach (var match in searchResult.Matches
                         .Where(match => !string.Equals(match.FilePath, context.ChangedFile.Path, StringComparison.Ordinal))
                         .Take(callerBudget))
            {
                var matchText = TrimToBudget(match.MatchText, this._options.MaxPrefetchRegionChars, out var matchTruncated);
                if (string.IsNullOrWhiteSpace(matchText))
                {
                    continue;
                }

                evidence.Add(
                    new PrefetchedContextEvidenceItem(
                        "supported_caller_site",
                        $"Related caller site: {match.FilePath}",
                        match.LineNumber.HasValue ? $"{match.FilePath}:L{match.LineNumber.Value}" : match.FilePath,
                        matchText,
                        matchTruncated || match.Truncated));
            }
        }

        if (evidence.Count == 0)
        {
            return context;
        }

        context.FileReviewContext.PerFileHint = fileHint with
        {
            PrefetchedContextEvidence = evidence,
        };

        var recorder = this._protocolRecorder ?? context.FileReviewContext.ProtocolRecorder;
        if (context.ProtocolId.HasValue && recorder is not null)
        {
            await recorder.RecordReviewStrategyEventAsync(
                context.ProtocolId.Value,
                ReviewProtocolEventNames.ContextPrefetchApplied,
                JsonSerializer.Serialize(
                    new
                    {
                        filePath = context.ChangedFile.Path,
                        evidenceCount = evidence.Count,
                        callerSiteCount = evidence.Count(item => string.Equals(item.Kind, "supported_caller_site", StringComparison.Ordinal)),
                        windowCount,
                        firstWindowStartLine,
                        windowedInjection,
                    }),
                JsonSerializer.Serialize(
                    evidence.Select(item => new
                    {
                        item.Kind,
                        item.Title,
                        item.SourceId,
                        item.Truncated,
                    })),
                null,
                cancellationToken);
        }

        return context;
    }

    /// <summary>
    ///     Builds the surrounding context evidence for the changed file.
    ///     When the full content fits within the budget, returns the whole file.
    ///     Otherwise, extracts hunk-centered windows from the diff and renders them with line-range markers.
    ///     Falls back to head-trim on any parse failure.
    /// </summary>
    internal static (string Content, bool Truncated, bool WindowedInjection, int WindowCount, int? FirstWindowStartLine)
        BuildSurroundingContext(
            string? fullContent,
            string? unifiedDiff,
            AiReviewOptions options)
    {
        if (string.IsNullOrWhiteSpace(fullContent))
        {
            return (string.Empty, false, false, 0, null);
        }

        var normalized = fullContent.Trim();
        var maxChars = options.MaxPrefetchRegionChars;

        // If the whole file fits within budget, inject it (unchanged behavior).
        if (normalized.Length <= maxChars)
        {
            return (normalized, false, false, 0, null);
        }

        // Try hunk-centered windowed injection.
        try
        {
            var hunkRanges = ReviewDiffProcessor.ExtractChangedNewLineRanges(unifiedDiff);
            if (hunkRanges.Count > 0)
            {
                var lines = normalized.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                var totalLines = lines.Length;
                var windows = BuildExpandedWindows(hunkRanges, lines, options.PrefetchWindowLinesBefore, options.PrefetchWindowLinesAfter, totalLines);
                var merged = MergeWindows(windows);

                var (content, truncated) = RenderWindows(merged, lines, totalLines, maxChars);
                var firstStart = merged.Count > 0 ? (int?)merged[0].Start : null;
                return (content, truncated, true, merged.Count, firstStart);
            }
        }
        catch
        {
            // On any parse failure, fall through to head-trim (never throw, never drop evidence).
        }

        // Fallback: head-trim (original behavior).
        return (normalized[..maxChars].TrimEnd(), true, false, 0, null);
    }

    /// <summary>
    ///     Expands each hunk range by <paramref name="linesBefore" /> / <paramref name="linesAfter" />,
    ///     then snaps the window start upward (max 30 extra lines) to the nearest structural boundary line.
    /// </summary>
    private static List<(int Start, int End)> BuildExpandedWindows(
        IReadOnlyList<(int Start, int End)> hunkRanges,
        string[] lines,
        int linesBefore,
        int linesAfter,
        int totalLines)
    {
        var windows = new List<(int Start, int End)>(hunkRanges.Count);
        foreach (var (hunkStart, hunkEnd) in hunkRanges)
        {
            // Convert to 0-based indices for array access.
            var rawStart = Math.Max(0, hunkStart - 1 - linesBefore);
            var rawEnd = Math.Min(totalLines - 1, hunkEnd - 1 + linesAfter);

            // Snap start upward (toward lower line numbers, i.e. search backward from rawStart)
            // to the nearest boundary line: column-0 non-whitespace or following a blank line.
            // Max 30 extra lines of snap.
            // TODO R-6: replace with Tree-sitter structural boundary resolver.
            var snappedStart = rawStart;
            for (var snap = 0; snap < 30 && snappedStart > 0; snap++)
            {
                var line = lines[snappedStart];
                var prevLine = lines[snappedStart - 1];
                var isBoundary = (line.Length > 0 && !char.IsWhiteSpace(line[0])) || string.IsNullOrWhiteSpace(prevLine);
                if (isBoundary)
                {
                    break;
                }

                snappedStart--;
            }

            windows.Add((snappedStart, rawEnd));
        }

        return windows;
    }

    private static List<(int Start, int End)> MergeWindows(List<(int Start, int End)> windows)
    {
        if (windows.Count == 0)
        {
            return windows;
        }

        windows.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(int Start, int End)>();
        var (ms, me) = windows[0];
        for (var i = 1; i < windows.Count; i++)
        {
            var (s, e) = windows[i];
            if (s <= me + 1)
            {
                me = Math.Max(me, e);
            }
            else
            {
                merged.Add((ms, me));
                ms = s;
                me = e;
            }
        }

        merged.Add((ms, me));
        return merged;
    }

    private static (string Content, bool Truncated) RenderWindows(
        List<(int Start, int End)> windows,
        string[] lines,
        int totalLines,
        int maxChars)
    {
        var sb = new StringBuilder(maxChars);
        var truncated = false;

        foreach (var (start, end) in windows)
        {
            // 1-based line markers
            var markerLine = $"[lines {start + 1}-{Math.Min(end + 1, totalLines)} of {totalLines}]";
            if (sb.Length > 0)
            {
                sb.AppendLine();
            }

            sb.AppendLine(markerLine);

            for (var i = start; i <= end && i < lines.Length; i++)
            {
                sb.AppendLine(lines[i]);

                if (sb.Length >= maxChars)
                {
                    truncated = true;
                    break;
                }
            }

            if (truncated)
            {
                break;
            }
        }

        var content = sb.ToString().TrimEnd();
        if (content.Length > maxChars)
        {
            content = content[..maxChars].TrimEnd();
            truncated = true;
        }

        return (content, truncated);
    }

    private static string TrimToBudget(string? content, int maxChars, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var normalized = content.Trim();
        if (normalized.Length <= maxChars)
        {
            return normalized;
        }

        truncated = true;
        return normalized[..maxChars].TrimEnd();
    }
}

internal sealed class FileByFileRiskMarkerStage : IReviewPipelineStage<PerFileReviewContext>
{
    public const string StageIdConstant = "file-by-file.risk-marker";

    private static readonly IReadOnlyList<(string MarkerId, string Pattern, bool IsSecurity)> MarkerRules =
    [
        ("security.auth-token", "token|oauth|jwt|bearer|secret|apikey|api_key|password|cookie", true),
        ("security.url-redirect", "redirect|returnurl|callbackurl|open\\(|window\\.open|location\\.|iframe|x-frame-options|frame-ancestors|origin|referer",
            true),
        ("security.allow-deny", "allowlist|denylist|whitelist|blacklist|regex.*domain|domain.*regex|cors", true),
        ("concurrency.async-loop", "foreach\\s*\\(\\s*async|promise\\.all|task\\.whenall|await foreach|goroutine|go\\s+func", false),
        ("concurrency.locking", "lock\\s*\\(|semaphore|mutex|monitor\\.|interlocked", false),
        ("concurrency.shared-counter", "\\+\\+|--|updatemany|cache.?key", false),
    ];

    public string StageId => StageIdConstant;

    public Task<PerFileReviewContext> ExecuteAsync(PerFileReviewContext context, CancellationToken cancellationToken)
    {
        if (context.FileReviewContext.PerFileHint is null)
        {
            return Task.FromResult(context);
        }

        var diff = context.ChangedFile.UnifiedDiff;
        if (string.IsNullOrWhiteSpace(diff))
        {
            return Task.FromResult(context);
        }

        var matchedMarkers = MarkerRules
            .Where(rule => Regex.IsMatch(diff, rule.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .Select(rule => (rule.MarkerId, rule.IsSecurity))
            .ToList();

        if (matchedMarkers.Count == 0)
        {
            context.FileReviewContext.PerFileHint = context.FileReviewContext.PerFileHint with
            {
                RiskMarkers = FileRiskMarkers.None,
            };
            return Task.FromResult(context);
        }

        context.FileReviewContext.PerFileHint = context.FileReviewContext.PerFileHint with
        {
            RiskMarkers = new FileRiskMarkers(
                matchedMarkers.Any(marker => marker.IsSecurity),
                matchedMarkers.Any(marker => !marker.IsSecurity),
                matchedMarkers.Select(marker => marker.MarkerId).Distinct(StringComparer.Ordinal).ToArray()),
        };

        return Task.FromResult(context);
    }
}

internal sealed class FileByFileProRvPrefilterStage(
    IProtocolRecorder protocolRecorder,
    IProRVPrefilter? proRvPrefilter,
    IAiConnectionRepository? aiConnectionRepository,
    IAiChatClientFactory? aiClientFactory,
    IAiRuntimeResolver? aiRuntimeResolver,
    ILogger<FileByFileProRvPrefilterStage> logger) : IReviewPipelineStage<PerFileReviewContext>
{
    // This id is part of persisted/profile-selected Reviewing protocol identity.
    public const string StageIdConstant = "file-by-file.prorv-prefilter";

    public string StageId => StageIdConstant;

    public async Task<PerFileReviewContext> ExecuteAsync(PerFileReviewContext context, CancellationToken cancellationToken)
    {
        if (context.ReviewResult is not null ||
            context.FileReviewContext.PerFileHint is null ||
            !context.FileReviewContext.EnableProRV ||
            (context.FileReviewContext.AugmentationMode != ReviewAugmentationMode.EarlySteering
             && context.FileReviewContext.PassKind != ReviewPassKind.ProRVAugmentation))
        {
            return context;
        }

        var fallbackChatClient = context.FileReviewContext.TierChatClient ?? context.FileReviewContext.DefaultReviewChatClient;
        if (fallbackChatClient is null)
        {
            return context;
        }

        var focusedReviewGuidance = await ProRVFocusedReviewGuidanceResolver.TryResolveAsync(
            context.Job,
            context.ChangedFile,
            context.FileReviewContext,
            fallbackChatClient,
            context.ProtocolId,
            protocolRecorder,
            proRvPrefilter,
            aiConnectionRepository,
            aiClientFactory,
            aiRuntimeResolver,
            logger,
            StageIdConstant,
            cancellationToken);

        if (focusedReviewGuidance.Guidance.Count == 0)
        {
            return context;
        }

        context.FileReviewContext.PerFileHint = context.FileReviewContext.PerFileHint with
        {
            FocusedReviewGuidance = focusedReviewGuidance.Guidance,
        };

        return context;
    }
}
