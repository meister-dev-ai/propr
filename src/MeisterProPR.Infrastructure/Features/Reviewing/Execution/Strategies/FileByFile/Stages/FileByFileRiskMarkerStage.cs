// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using System.Text.RegularExpressions;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Features.Reviewing.Execution.Services;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.CodeAnalysis;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

internal sealed class FileByFileRiskMarkerStage(IStructuralCodeAnalyzer? analyzer = null)
    : IReviewPipelineStage<PerFileReviewContext>
{
    public const string StageIdConstant = "file-by-file.risk-marker";

    // Security markers only. Matching runs against the diff's ADDED CONTENT, never the raw diff — so file
    // headers (`+++`/`---`), hunk headers, context, and removed lines cannot flag a file. When a structural
    // analyzer can parse the file, matching is further narrowed to added CODE (comments/strings excluded).
    private static readonly IReadOnlyList<(string MarkerId, string Pattern)> MarkerRules =
    [
        ("security.auth-token", "token|oauth|jwt|bearer|secret|apikey|api_key|password|cookie"),
        ("security.url-redirect", "redirect|returnurl|callbackurl|open\\(|window\\.open|location\\.|iframe|x-frame-options|frame-ancestors|origin|referer"),
        ("security.allow-deny", "allowlist|denylist|whitelist|blacklist|regex.*domain|domain.*regex|cors"),
    ];

    private readonly IStructuralCodeAnalyzer? _analyzer = analyzer;

    public string StageId => StageIdConstant;

    public async Task<PerFileReviewContext> ExecuteAsync(PerFileReviewContext context, CancellationToken cancellationToken)
    {
        if (context.FileReviewContext.PerFileHint is null)
        {
            return context;
        }

        var matchText = await this.ResolveMatchTextAsync(
                context.ChangedFile.Path,
                context.ChangedFile.FullContent,
                context.ChangedFile.UnifiedDiff,
                cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(matchText))
        {
            context.FileReviewContext.PerFileHint = context.FileReviewContext.PerFileHint with
            {
                RiskMarkers = FileRiskMarkers.None,
            };
            return context;
        }

        var matchedMarkers = MarkerRules
            .Where(rule => Regex.IsMatch(matchText, rule.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .Select(rule => rule.MarkerId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        context.FileReviewContext.PerFileHint = context.FileReviewContext.PerFileHint with
        {
            RiskMarkers = matchedMarkers.Length == 0
                ? FileRiskMarkers.None
                : new FileRiskMarkers(true, matchedMarkers),
        };

        return context;
    }

    private async Task<string> ResolveMatchTextAsync(string path, string fullContent, string unifiedDiff, CancellationToken ct)
    {
        var addedContent = ReviewDiffProcessor.ExtractAddedContent(unifiedDiff);
        if (string.IsNullOrWhiteSpace(addedContent))
        {
            return string.Empty;
        }

        // When a structural backend can parse this file, narrow the match to the ADDED lines' real code
        // (comments and string literals blanked). Anything missing/faulting falls back to plain added content.
        if (this._analyzer is null
            || !this._analyzer.CanAnalyze(path)
            || string.IsNullOrEmpty(fullContent)
            || LanguagePaths.TryResolve(path) is not { } language)
        {
            return addedContent;
        }

        string codeOnlyFull;
        try
        {
            var request = new StructuralParseRequest(path, language, fullContent, []);
            codeOnlyFull = await this._analyzer.ExtractCodeTextAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return addedContent;
        }

        if (string.IsNullOrEmpty(codeOnlyFull))
        {
            return addedContent;
        }

        var insertedLines = ReviewDiffProcessor.GetInsertedNewLineNumbers(unifiedDiff);
        if (insertedLines.Count == 0)
        {
            return string.Empty;
        }

        var codeLines = codeOnlyFull.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var builder = new StringBuilder();
        foreach (var lineNumber in insertedLines.Where(lineNumber => lineNumber >= 1 && lineNumber <= codeLines.Length))
        {
            builder.Append(codeLines[lineNumber - 1]).Append('\n');
        }

        return builder.ToString();
    }
}
