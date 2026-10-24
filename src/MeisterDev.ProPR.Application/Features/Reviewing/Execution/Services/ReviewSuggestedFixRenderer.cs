// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Markdig;
using Markdig.Syntax;
using MeisterDev.ProPR.CodeAnalysis;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>Renders validated replacement code and prevents unstructured fences from becoming applicable edits.</summary>
public static class ReviewSuggestedFixRenderer
{
    private const string StartMarker = "<!-- propr-suggested-fix:start -->";
    private const string EndMarker = "<!-- propr-suggested-fix:end -->";

    /// <summary>Checks source identity and anchor containment before a provider builds an exact range.</summary>
    public static bool CanApply(ReviewComment comment, string? headSha) =>
        comment.SuggestedFix is
            { SourceHeadSha: not null, SourceFilePath: not null, ReplacementText: not null, IsSourceValidated: true, IsAnchorRemapped: false } fix
        && fix.ReplacementText.Length <= ReviewSuggestedFixValidator.MaximumCodeCharacters
        && fix.RejectionReason is null && ReviewSuggestedFixValidator.ChangesSource(fix)
        && !string.IsNullOrWhiteSpace(headSha) && string.Equals(fix.SourceHeadSha, headSha, StringComparison.Ordinal)
        && string.Equals(fix.SourceFilePath, ReviewSuggestedFixValidator.NormalizePath(comment.FilePath), StringComparison.Ordinal)
        && fix.SourceAnchorLineNumber == comment.LineNumber
        && fix.StartLine > 0 && fix.EndLine >= fix.StartLine && comment.LineNumber >= fix.StartLine && comment.LineNumber <= fix.EndLine;

    /// <summary>Formats replacement bytes outside the raw claim; applicability must be established by the publisher.</summary>
    public static string RenderMessage(ReviewComment comment, bool applicable, string suggestionInfo = "suggestion")
    {
        var message = DowngradeUnstructuredSuggestions(ReviewFindingTextFormatter.RenderText(comment.Message));
        if (comment.SuggestedFix is not { ReplacementText: not null } fix
            || fix.ReplacementText.Length > ReviewSuggestedFixValidator.MaximumCodeCharacters
            || fix.RejectionReason is not null || !ReviewSuggestedFixValidator.ChangesSource(fix))
        {
            return message;
        }

        applicable = applicable && CanApply(comment, fix.SourceHeadSha);

        var longest = 0;
        var current = 0;
        foreach (var character in fix.ReplacementText)
        {
            current = character == '`' ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        var fence = new string('`', Math.Max(3, longest + 1));
        var info = applicable ? suggestionInfo : ResolveFenceLanguage(fix.SourceFilePath ?? comment.FilePath);
        var label = applicable ? string.Empty : "**Suggested change**\n\n";
        return ReviewFindingTextFormatter.CloseOpenFences(message) + "\n\n" + StartMarker + "\n\n" + label + fence + info
               + "\n" + fix.ReplacementText + (fix.ReplacementText.EndsWith('\n') ? string.Empty : "\n") + fence + "\n\n" + EndMarker;
    }

    private static string ResolveFenceLanguage(string? path) => LanguagePaths.TryResolve(path) switch
    {
        SupportedLanguage.CSharp => "csharp",
        SupportedLanguage.TypeScript => "typescript",
        SupportedLanguage.Tsx => "tsx",
        SupportedLanguage.JavaScript => "javascript",
        SupportedLanguage.Python => "python",
        SupportedLanguage.Go => "go",
        SupportedLanguage.Java => "java",
        SupportedLanguage.Ruby => "ruby",
        _ => string.Empty,
    };

    /// <summary>Removes only the generated code suffix when restoring the raw finding for deduplication.</summary>
    internal static string StripGeneratedFix(string message)
    {
        if (!message.TrimEnd().EndsWith(EndMarker, StringComparison.Ordinal))
        {
            return message;
        }

        var marker = Markdown.Parse(message).OfType<HtmlBlock>()
            .FirstOrDefault(block => block.Span.Start >= 2 && message.AsSpan(block.Span.Start).StartsWith(StartMarker, StringComparison.Ordinal)
                                                           && message[block.Span.Start - 2] == '\n' && message[block.Span.Start - 1] == '\n');
        return marker is null ? message : message[..(marker.Span.Start - 2)];
    }

    internal static bool TryLocateGeneratedFix(string message, out int start, out int end)
    {
        start = end = -1;
        foreach (var block in Markdown.Parse(message).OfType<HtmlBlock>())
        {
            if (start < 0 && block.Span.Start >= 2 && message.AsSpan(block.Span.Start).StartsWith(StartMarker, StringComparison.Ordinal))
            {
                start = block.Span.Start - 2;
            }
            else if (start >= 0 && message.AsSpan(block.Span.Start).StartsWith(EndMarker, StringComparison.Ordinal))
            {
                end = block.Span.Start + EndMarker.Length;
                return true;
            }
        }

        return false;
    }

    /// <summary>Removes actionable fence labels using Markdown structure without changing code contents.</summary>
    public static string DowngradeUnstructuredSuggestions(string message)
    {
        var blocks = Markdown.Parse(message).Descendants().OfType<FencedCodeBlock>()
            .Where(block => block.Info?.StartsWith("suggestion", StringComparison.OrdinalIgnoreCase) == true)
            .OrderByDescending(block => block.Span.Start);
        foreach (var block in blocks)
        {
            var lineEnd = message.IndexOfAny(['\r', '\n'], block.Span.Start);
            lineEnd = lineEnd < 0 ? message.Length : lineEnd;
            var opening = message[block.Span.Start..lineEnd];
            var infoStart = opening.IndexOf("suggestion", StringComparison.OrdinalIgnoreCase);
            if (infoStart >= 0)
            {
                message = message.Remove(block.Span.Start + infoStart, lineEnd - block.Span.Start - infoStart);
            }
        }

        return message;
    }
}
