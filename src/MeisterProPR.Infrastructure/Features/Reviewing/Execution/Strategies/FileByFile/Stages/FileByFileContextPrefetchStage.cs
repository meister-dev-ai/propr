// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Features.Reviewing.Execution.Services;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.CodeAnalysis;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

internal sealed class FileByFileContextPrefetchStage(
    AiReviewOptions? options = null,
    IProtocolRecorder? protocolRecorder = null,
    IStructuralCodeAnalyzer? analyzer = null)
    : IReviewPipelineStage<PerFileReviewContext>
{
    public const string StageIdConstant = "file-by-file.context-prefetch";

    // T030 — observability counters for boundary-resolution outcomes (Principle V).
    // Incremented at the trace-emit site so operators can see tree-sitter vs each
    // FallbackReason distribution across reviews.
    private static readonly Meter BoundaryMeter = new("MeisterProPR.Reviewing.Prefetch", "1.0.0");

    private static readonly Counter<int> BoundaryOutcomeCounter =
        BoundaryMeter.CreateCounter<int>(
            "reviewing.prefetch.boundary_outcomes",
            "{outcome}",
            "Context-prefetch boundary-resolution outcomes: tree-sitter or a FallbackReason.");

    private readonly IStructuralCodeAnalyzer? _analyzer = analyzer;

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
        var surrounding = await BuildSurroundingContextAsync(
            context.ChangedFile.Path,
            context.ChangedFile.FullContent,
            context.ChangedFile.UnifiedDiff,
            this._options,
            this._analyzer,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(surrounding.RenderedContent))
        {
            evidence.Add(
                new PrefetchedContextEvidenceItem(
                    "surrounding_definition",
                    "Changed definition context",
                    context.ChangedFile.Path,
                    surrounding.RenderedContent,
                    surrounding.Truncated));
        }

        var fanOut = await this.CollectCallerEvidenceAsync(context, reviewTools, evidence, cancellationToken);

        // Persist the fan-out signal regardless of caller evidence; the triage decision reads it later.
        context.FileReviewContext.PerFileHint = fileHint with
        {
            PrefetchedContextEvidence = evidence.Count > 0 ? evidence : fileHint.PrefetchedContextEvidence,
            FanOut = fanOut,
        };

        if (evidence.Count == 0)
        {
            return context;
        }

        await this.RecordContextPrefetchTraceAsync(context, evidence, surrounding, cancellationToken);

        return context;
    }

    private async Task<FanOutSignal> CollectCallerEvidenceAsync(
        PerFileReviewContext context,
        IReviewContextTools reviewTools,
        List<PrefetchedContextEvidenceItem> evidence,
        CancellationToken cancellationToken)
    {
        var callerBudget = Math.Max(0, this._options.MaxPrefetchCallerSites);
        if (callerBudget <= 0)
        {
            return FanOutSignal.Unavailable;
        }

        if (this._options.EnableStructuralReferenceTools && this._analyzer is not null)
        {
            // Deterministic caller-evidence feed driven by the CHANGED definitions in the changed
            // file. Independent of whether the model invokes a tool. Also yields the blast-radius signal.
            return await this.AppendChangedSymbolCallerEvidenceAsync(context, reviewTools, evidence, callerBudget, cancellationToken);
        }

        // Fallback: path-keyed related_symbol caller evidence (kill-switch off).
        await AppendRelatedSymbolCallerEvidenceAsync(context, reviewTools, evidence, callerBudget, this._options, cancellationToken);
        return FanOutSignal.Unavailable;
    }

    private async Task RecordContextPrefetchTraceAsync(
        PerFileReviewContext context,
        List<PrefetchedContextEvidenceItem> evidence,
        StructuralContextResult surrounding,
        CancellationToken cancellationToken)
    {
        var recorder = this._protocolRecorder ?? context.FileReviewContext.ProtocolRecorder;
        if (!context.ProtocolId.HasValue || recorder is null)
        {
            return;
        }

        // Bump the boundary-outcome counter so operators can see the tree-sitter vs
        // heuristic distribution across reviews.
        var outcomeTag = surrounding.BoundaryResolved
            ? "tree-sitter"
            : surrounding.FallbackReason?.ToTraceString() ?? "heuristic";
        BoundaryOutcomeCounter.Add(
            1,
            new KeyValuePair<string, object?>("outcome", outcomeTag),
            new KeyValuePair<string, object?>("language", ResolveSupportedLanguage(context.ChangedFile.Path)?.ToString() ?? "unknown"));

        await recorder.RecordReviewStrategyEventAsync(
            context.ProtocolId.Value,
            ReviewProtocolEventNames.ContextPrefetchApplied,
            JsonSerializer.Serialize(
                new
                {
                    filePath = context.ChangedFile.Path,
                    evidenceCount = evidence.Count,
                    callerSiteCount = evidence.Count(item => string.Equals(item.Kind, "supported_caller_site", StringComparison.Ordinal)),
                    windowCount = surrounding.WindowCount,
                    firstWindowStartLine = surrounding.FirstWindowStartLine,
                    windowedInjection = surrounding.BoundaryResolved || surrounding.WindowCount > 0,
                    // Structural-boundary trace fields:
                    boundaryResolver = surrounding.BoundaryResolved ? "tree-sitter" : "heuristic",
                    enclosingSymbol = surrounding.EnclosingSymbol,
                    enclosingKind = surrounding.EnclosingKind?.ToTraceString(),
                    fallbackReason = surrounding.FallbackReason?.ToTraceString(),
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

    /// <summary>
    ///     Derive the changed file's changed definitions structurally, then inject a bounded set of
    ///     <c>supported_caller_site</c> evidence items for confirmed cross-file callers of those symbols —
    ///     comment/string occurrences excluded. Deterministic; does not depend on the model invoking a
    ///     tool. Never throws to the review.
    /// </summary>
    private async Task<FanOutSignal> AppendChangedSymbolCallerEvidenceAsync(
        PerFileReviewContext context,
        IReviewContextTools reviewTools,
        List<PrefetchedContextEvidenceItem> evidence,
        int callerBudget,
        CancellationToken ct)
    {
        var path = context.ChangedFile.Path;
        var content = context.ChangedFile.FullContent;
        if (this._analyzer is null || !this._analyzer.CanAnalyze(path) || string.IsNullOrEmpty(content))
        {
            return FanOutSignal.Unavailable;
        }

        if (LanguagePaths.TryResolve(path) is not { } language)
        {
            return FanOutSignal.Unavailable;
        }

        IReadOnlyList<(int Start, int End)> hunkRanges;
        try
        {
            hunkRanges = ReviewDiffProcessor.ExtractChangedNewLineRanges(context.ChangedFile.UnifiedDiff);
        }
        catch
        {
            return FanOutSignal.Unavailable;
        }

        if (hunkRanges.Count == 0)
        {
            return FanOutSignal.Unavailable;
        }

        var changedRanges = hunkRanges.Select(r => new ChangedLineRange(r.Start, r.End)).ToList();
        var request = new StructuralParseRequest(path, language, content, changedRanges);

        IReadOnlyList<EnclosingDefinition> enclosing;
        try
        {
            enclosing = await this._analyzer.ResolveEnclosingDefinitionsAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return FanOutSignal.Unavailable;
        }

        var changedSymbols = enclosing
            .Select(e => e.Name)
            .Where(static n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var injected = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Blast-radius accumulation across ALL changed symbols (independent of the evidence-injection budget):
        // total confirmed references, whether any lookup truncated, and whether any lookup produced data.
        var totalReferences = 0;
        var anyTruncated = false;
        var gotData = false;

        foreach (var symbol in changedSymbols)
        {
            var outcome = await CollectCallerSitesForSymbolAsync(
                symbol!,
                path,
                reviewTools,
                evidence,
                seen,
                injected,
                callerBudget,
                ct).ConfigureAwait(false);
            if (outcome is null)
            {
                continue;
            }

            gotData = true;
            totalReferences += outcome.Value.ReferenceCount;
            anyTruncated |= outcome.Value.Truncated;
            injected = outcome.Value.Injected;
        }

        if (!gotData)
        {
            return FanOutSignal.Unavailable;
        }

        return anyTruncated ? FanOutSignal.Truncated(totalReferences) : FanOutSignal.Measured(totalReferences);
    }

    /// <summary>
    ///     Looks up confirmed cross-file callers for a single changed symbol and injects up to the
    ///     remaining caller budget as evidence. Returns <see langword="null" /> when the lookup itself
    ///     produced no usable data (unavailable or cancelled-and-swallowed failure), so the caller can
    ///     tell "no data" apart from "zero references found".
    /// </summary>
    private static async Task<(int ReferenceCount, bool Truncated, int Injected)?> CollectCallerSitesForSymbolAsync(
        string symbol,
        string path,
        IReviewContextTools reviewTools,
        List<PrefetchedContextEvidenceItem> evidence,
        HashSet<string> seen,
        int injected,
        int callerBudget,
        CancellationToken ct)
    {
        ReferenceLookupResult references;
        try
        {
            references = await reviewTools.FindReferencesAsync(new SymbolReferenceQuery(symbol), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }

        if (references.Unavailable)
        {
            return null;
        }

        foreach (var site in references.Sites.Where(s => !string.Equals(s.FilePath, path, StringComparison.Ordinal)))
        {
            if (injected >= callerBudget)
            {
                break;
            }

            var key = $"{site.FilePath}:{site.Line}";
            if (!seen.Add(key))
            {
                continue;
            }

            // Carry the snippet + enclosing symbol the reference lookup already resolved, so the reviewer
            // sees the caller line and its enclosing definition without re-fetching the file.
            var enclosing = string.IsNullOrWhiteSpace(site.EnclosingName) ? null : site.EnclosingName;
            var snippet = string.IsNullOrWhiteSpace(site.LineSnippet) ? null : site.LineSnippet;
            var enclosingClause = enclosing is null ? string.Empty : $" in `{enclosing}`";
            var snippetClause = snippet is null ? string.Empty : $"\n    {snippet}";

            evidence.Add(
                new PrefetchedContextEvidenceItem(
                    "supported_caller_site",
                    $"Confirmed caller of {symbol}: {site.FilePath}",
                    $"{site.FilePath}:L{site.Line}",
                    $"Confirmed cross-file caller of `{symbol}` at {site.FilePath}:{site.Line}{enclosingClause} (structural; comment/string occurrences excluded).{snippetClause}",
                    false,
                    snippet,
                    enclosing));
            injected++;
        }

        return (references.Sites.Count, references.Truncated, injected);
    }

    /// <summary>
    ///     Path-keyed <c>related_symbol</c> caller evidence. Retained as the fallback when the
    ///     structural reference surface is disabled (kill-switch off).
    /// </summary>
    private static async Task AppendRelatedSymbolCallerEvidenceAsync(
        PerFileReviewContext context,
        IReviewContextTools reviewTools,
        List<PrefetchedContextEvidenceItem> evidence,
        int callerBudget,
        AiReviewOptions options,
        CancellationToken cancellationToken)
    {
        var searchResult = await reviewTools.SearchCodeAsync(
            new CodeSearchRequest(
                context.ChangedFile.Path,
                CodeSearchModes.RelatedSymbol,
                RepositorySearchBranchSides.Source,
                RepositorySearchPathScopes.Repository),
            cancellationToken);

        if (searchResult is not { Status: RepositorySearchStatuses.Success or RepositorySearchStatuses.Partial })
        {
            return;
        }

        foreach (var match in searchResult.Matches
                     .Where(match => !string.Equals(match.FilePath, context.ChangedFile.Path, StringComparison.Ordinal))
                     .Take(callerBudget))
        {
            var matchText = TrimToBudget(match.MatchText, options.MaxPrefetchRegionChars, out var matchTruncated);
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

    /// <summary>
    ///     Builds the surrounding context evidence for the changed file.
    ///     When the full content fits within the budget, returns the whole file.
    ///     Otherwise, extracts hunk-centered windows from the diff and renders them with line-range markers.
    ///     Falls back to head-trim on any parse failure.
    /// </summary>
    /// <remarks>
    ///     Heuristic-only entry point retained for backward compatibility with existing
    ///     tests and the kill-switch parity check. The prefetch stage now routes through
    ///     <see cref="BuildSurroundingContextAsync" /> which adds the structural boundary path.
    /// </remarks>
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
    ///     Boundary-aware surrounding context (feature 070). Tries the structural analyzer
    ///     first; on any fallback reason returns the heuristic window with the reason set.
    ///     Never throws - parse/timeout problems are contained by the analyzer and reported
    ///     via <see cref="StructuralContextResult.FallbackReason" />.
    /// </summary>
    internal static async Task<StructuralContextResult> BuildSurroundingContextAsync(
        string path,
        string? fullContent,
        string? unifiedDiff,
        AiReviewOptions options,
        IStructuralCodeAnalyzer? analyzer,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fullContent))
        {
            return new StructuralContextResult(string.Empty, false, false, 0, null, null, null, null);
        }

        var normalized = fullContent.Trim();
        var maxChars = options.MaxPrefetchRegionChars;

        // Whole-file injection when the file fits the budget. The trace records heuristic
        // (no boundary resolution needed) so the admin sees the same signal as today.
        if (normalized.Length <= maxChars)
        {
            return new StructuralContextResult(normalized, false, false, 0, null, null, null, null);
        }

        // Kill-switch: when the structural feature is disabled, go straight to the heuristic.
        if (!options.EnableStructuralBoundaryResolution)
        {
            return HeuristicResult(normalized, unifiedDiff, options, FallbackReason.AnalyzerDisabled);
        }

        // No analyzer wired in (e.g. tests that only exercise the heuristic). Heuristic.
        if (analyzer is null)
        {
            return HeuristicResult(normalized, unifiedDiff, options, null);
        }

        if (!analyzer.IsAvailable)
        {
            return HeuristicResult(normalized, unifiedDiff, options, FallbackReason.NativeUnavailable);
        }

        if (!analyzer.CanAnalyze(path))
        {
            return HeuristicResult(normalized, unifiedDiff, options, FallbackReason.UnsupportedLanguage);
        }

        // Extract changed ranges from the diff. On failure, fall back to heuristic with no
        // specific reason (the heuristic itself will degrade to head-trim if needed).
        IReadOnlyList<(int Start, int End)> hunkRanges;
        try
        {
            hunkRanges = ReviewDiffProcessor.ExtractChangedNewLineRanges(unifiedDiff);
        }
        catch
        {
            return HeuristicResult(normalized, unifiedDiff, options, null);
        }

        if (hunkRanges.Count == 0)
        {
            return HeuristicResult(normalized, unifiedDiff, options, FallbackReason.NoEnclosingDefinition);
        }

        // Pre-parse size guard (R8 step 1). The analyzer enforces the same guard internally,
        // but checking it here lets the trace record FileTooLarge without an extra parse
        // round-trip and keeps the contract reason accurate.
        int sourceByteCount;
        try
        {
            sourceByteCount = Encoding.UTF8.GetByteCount(fullContent);
        }
        catch
        {
            return HeuristicResult(normalized, unifiedDiff, options, FallbackReason.ParseFault);
        }

        if (sourceByteCount > options.MaxStructuralParseBytes)
        {
            return HeuristicResult(normalized, unifiedDiff, options, FallbackReason.FileTooLarge);
        }

        // Parse + resolve enclosing definitions via the structural analyzer.
        var language = ResolveSupportedLanguage(path);
        if (language is null)
        {
            return HeuristicResult(normalized, unifiedDiff, options, FallbackReason.UnsupportedLanguage);
        }

        var changedRanges = hunkRanges
            .Select(r => new ChangedLineRange(r.Start, r.End))
            .ToList();

        var request = new StructuralParseRequest(path, language.Value, fullContent, changedRanges);

        IReadOnlyList<EnclosingDefinition> definitions;
        try
        {
            definitions = await analyzer.ResolveEnclosingDefinitionsAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return HeuristicResult(normalized, unifiedDiff, options, FallbackReason.ParseFault);
        }

        if (definitions.Count == 0)
        {
            return HeuristicResult(normalized, unifiedDiff, options, FallbackReason.NoEnclosingDefinition);
        }

        // Render the enclosing definitions as windows, reusing the existing [lines X-Y of N]
        // markers and the MaxPrefetchRegionChars budget (R4).
        var lines = normalized.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var totalLines = lines.Length;
        var windows = definitions
            .Select(d => (Start: Math.Max(0, d.StartLine - 1), End: Math.Min(totalLines - 1, d.EndLine - 1)))
            .ToList();
        var merged = MergeWindows(windows);

        var (content, truncated) = RenderWindows(merged, lines, totalLines, maxChars);
        var firstStart = merged.Count > 0 ? (int?)merged[0].Start + 1 : null;
        var enclosing = definitions.FirstOrDefault();
        var enclosingSymbol = enclosing?.Name;
        var enclosingKind = enclosing?.Kind;

        return new StructuralContextResult(
            content,
            truncated,
            true,
            merged.Count,
            firstStart,
            enclosingSymbol,
            enclosingKind,
            null);
    }

    private static StructuralContextResult HeuristicResult(
        string normalized,
        string? unifiedDiff,
        AiReviewOptions options,
        FallbackReason? reason)
    {
        var (content, truncated, windowedInjection, windowCount, firstWindowStartLine) =
            BuildSurroundingContext(normalized, unifiedDiff, options);

        return new StructuralContextResult(
            content,
            truncated,
            false,
            windowCount,
            firstWindowStartLine,
            null,
            null,
            reason);
    }

    private static SupportedLanguage? ResolveSupportedLanguage(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.ToLowerInvariant() switch
        {
            ".ts" => SupportedLanguage.TypeScript,
            ".tsx" => SupportedLanguage.Tsx,
            ".js" or ".jsx" or ".mjs" or ".cjs" => SupportedLanguage.JavaScript,
            ".py" or ".pyi" => SupportedLanguage.Python,
            ".go" => SupportedLanguage.Go,
            ".java" => SupportedLanguage.Java,
            ".rb" => SupportedLanguage.Ruby,
            // C# resolves structurally through the composite (Roslyn-syntax backend). Any Roslyn
            // parse/timeout/empty result still degrades to the heuristic below, so C# within-file
            // context is never worse than the heuristic.
            ".cs" => SupportedLanguage.CSharp,
            _ => null,
        };
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
            // The structural analyzer (feature 070) replaces this heuristic snap with a
            // boundary-resolved window. Kept for the heuristic fallback path.
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
