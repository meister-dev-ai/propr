// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.RegularExpressions;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;

/// <summary>
///     Selects bounded excerpts of code for the evidence judge beyond the anchor window: the parts of the files the
///     reviewing pass read that contain a symbol the claim names, and the place in the anchor file where such a symbol
///     occurs when the anchor window does not show it. The reviewer's reads are re-read from the source branch at the
///     recorded ranges, because the review loop records the ranges and not the text. Every excerpt is cut to a fixed
///     number of lines and characters and marks the cut, and the number of excerpts is capped.
/// </summary>
internal static partial class EvidenceJudgeReviewerExcerpts
{
    /// <summary>Maximum number of excerpts taken from the reviewer's reads.</summary>
    internal const int MaxReviewerExcerpts = 3;

    /// <summary>Maximum number of lines of one excerpt.</summary>
    internal const int MaxExcerptLines = 60;

    /// <summary>Maximum number of characters of one excerpt, before the truncation marker.</summary>
    internal const int MaxExcerptChars = 3000;

    /// <summary>Maximum number of files a finding cites that are excerpted for a claim without an anchor file.</summary>
    internal const int MaxSupportingFiles = 3;

    /// <summary>Maximum number of distinct reviewer reads examined, taken from the most recent ones.</summary>
    internal const int MaxReadsExamined = 12;

    /// <summary>Maximum number of lines read from one reviewer read.</summary>
    internal const int MaxLinesPerRead = 1000;

    /// <summary>Maximum number of lines of the anchor file scanned for a claimed symbol outside the anchor window.</summary>
    internal const int MaxSameFileScanLines = 4000;

    /// <summary>Maximum number of symbols taken from one claim.</summary>
    internal const int MaxSymbols = 8;

    private const int MinSymbolLength = 3;

    private static readonly HashSet<string> IgnoredWords = new(StringComparer.Ordinal)
    {
        "null", "true", "false", "undefined", "none", "this", "self", "return", "await", "async", "new", "void", "var",
        "let", "const", "string", "int", "bool", "object", "list", "error", "value", "values", "data", "result",
    };

    /// <summary>
    ///     Returns the identifiers the claim names: the subject identifier and the identifiers inside code spans of the
    ///     assertion text, in that order, without duplicates and without words too short or too generic to locate code.
    /// </summary>
    internal static IReadOnlyList<string> ExtractClaimSymbols(ClaimDescriptor claim)
    {
        var symbols = new List<string>();
        AddIdentifiers(symbols, claim.SubjectIdentifier);
        foreach (Match span in CodeSpanPattern().Matches(claim.AssertionText ?? string.Empty))
        {
            AddIdentifiers(symbols, span.Groups["code"].Value);
        }

        return symbols.Take(MaxSymbols).ToList();
    }

    /// <summary>
    ///     Builds the excerpts for one claim.
    /// </summary>
    /// <param name="claim">The claim the judge decides.</param>
    /// <param name="reviewerReads">The ranges the reviewing pass read, in the order it read them.</param>
    /// <param name="shownStartLine">First anchor-file line the judge receives in the anchor window.</param>
    /// <param name="shownEndLine">Last anchor-file line the judge receives in the anchor window.</param>
    /// <param name="anchorWindowCoversFile">Whether the anchor window already shows the whole anchor file.</param>
    /// <param name="readFile">Reads a line range of a file from the source branch.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async Task<IReadOnlyList<JudgeExcerpt>> BuildAsync(
        ClaimDescriptor claim,
        IReadOnlyList<ReviewerFileRead> reviewerReads,
        int shownStartLine,
        int shownEndLine,
        bool anchorWindowCoversFile,
        Func<string, int, int, CancellationToken, Task<string>> readFile,
        CancellationToken ct)
    {
        var symbols = ExtractClaimSymbols(claim);
        if (symbols.Count == 0)
        {
            return [];
        }

        var patterns = symbols.Select(symbol => new Regex($@"\b{Regex.Escape(symbol)}\b", RegexOptions.CultureInvariant)).ToList();
        var anchorPath = NormalizePath(claim.AnchorFilePath);
        var excerpts = new List<JudgeExcerpt>();

        if (anchorPath is not null && !anchorWindowCoversFile)
        {
            var sameFile = await TryReadAsync(readFile, claim.AnchorFilePath!, 1, MaxSameFileScanLines, ct).ConfigureAwait(false);
            var excerpt = sameFile is null
                ? null
                : SameFileExcerpt(claim.AnchorFilePath!, SplitLines(sameFile), patterns, shownStartLine, shownEndLine);
            if (excerpt is not null)
            {
                excerpts.Add(excerpt);
            }
        }

        var candidates = new List<(JudgeExcerpt Excerpt, int Matches, int Order)>();

        // Reads of the anchor file inside the window are left out before the cap, because the judge already receives
        // that part of the file.
        bool IsAnchorFile(ReviewerFileRead read) => anchorPath is not null && string.Equals(NormalizePath(read.Path), anchorPath, StringComparison.Ordinal);
        var reads = reviewerReads
            .Where(read => read.EndLine >= read.StartLine && !string.IsNullOrWhiteSpace(read.Path))
            .Where(read => !(IsAnchorFile(read) && read.StartLine >= shownStartLine && read.EndLine <= shownEndLine))
            .Distinct()
            .TakeLast(MaxReadsExamined)
            .ToList();
        for (var order = 0; order < reads.Count; order++)
        {
            var read = reads[order];
            var isAnchorFile = IsAnchorFile(read);

            var endLine = Math.Min(read.EndLine, read.StartLine + MaxLinesPerRead - 1);
            var content = await TryReadAsync(readFile, read.Path, read.StartLine, endLine, ct).ConfigureAwait(false);
            if (content is null)
            {
                continue;
            }

            var excerpt = ExcerptAroundFirstMatch(
                read.Path,
                content,
                read.StartLine,
                patterns,
                line => !isAnchorFile || line < shownStartLine || line > shownEndLine);
            if (excerpt is null)
            {
                continue;
            }

            candidates.Add((excerpt, patterns.Count(pattern => pattern.IsMatch(excerpt.Text)), order));
        }

        // Excerpts that contain more of the claimed symbols come first; among equal ones the later read comes first,
        // because a reviewer usually reads the deciding code last. The excerpts are taken in that order, and one that
        // overlaps an excerpt already taken, including the same-file excerpt, is skipped, so an overlapping weaker
        // excerpt never displaces a stronger one.
        var taken = 0;
        foreach (var candidate in candidates.OrderByDescending(candidate => candidate.Matches).ThenByDescending(candidate => candidate.Order))
        {
            if (taken == MaxReviewerExcerpts)
            {
                break;
            }

            if (excerpts.Any(existing => Overlaps(existing, candidate.Excerpt)))
            {
                continue;
            }

            excerpts.Add(candidate.Excerpt);
            taken++;
        }

        return excerpts;
    }

    /// <summary>
    ///     Builds one excerpt of each file a finding cites, for a claim without an anchor file: the part around the
    ///     first line that contains a claimed symbol, or the start of the file when no line does. At most
    ///     <see cref="MaxSupportingFiles" /> files are read, each from its first line up to <see cref="MaxLinesPerRead" />
    ///     lines, and every excerpt has the same line and character bounds as a reviewer excerpt.
    /// </summary>
    /// <param name="claim">The claim the judge decides.</param>
    /// <param name="supportingFiles">The files the finding cites, in the order the finding names them.</param>
    /// <param name="readFile">Reads a line range of a file from the source branch.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async Task<IReadOnlyList<JudgeExcerpt>> BuildSupportingFileExcerptsAsync(
        ClaimDescriptor claim,
        IReadOnlyList<string> supportingFiles,
        Func<string, int, int, CancellationToken, Task<string>> readFile,
        CancellationToken ct)
    {
        var patterns = ExtractClaimSymbols(claim)
            .Select(symbol => new Regex($@"\b{Regex.Escape(symbol)}\b", RegexOptions.CultureInvariant))
            .ToList();
        var files = supportingFiles
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .DistinctBy(NormalizePath, StringComparer.Ordinal)
            .Take(MaxSupportingFiles)
            .ToList();
        var excerpts = new List<JudgeExcerpt>(files.Count);
        foreach (var path in files)
        {
            var content = await TryReadAsync(readFile, path, 1, MaxLinesPerRead, ct).ConfigureAwait(false);
            if (content is null)
            {
                continue;
            }

            var excerpt = ExcerptAroundFirstMatch(path, content, 1, patterns, _ => true)
                          ?? ExcerptAt(path, SplitLines(content), 1, 0, patterns);
            excerpts.Add(excerpt with { Origin = JudgeExcerpt.SupportingFileOrigin });
        }

        return excerpts;
    }

    private static void AddIdentifiers(List<string> symbols, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (Match identifier in IdentifierPattern().Matches(text))
        {
            var value = identifier.Value;
            if (value.Length >= MinSymbolLength && !IgnoredWords.Contains(value.ToLowerInvariant()) && !symbols.Contains(value, StringComparer.Ordinal))
            {
                symbols.Add(value);
            }
        }
    }

    private static async Task<string?> TryReadAsync(
        Func<string, int, int, CancellationToken, Task<string>> readFile,
        string path,
        int startLine,
        int endLine,
        CancellationToken ct)
    {
        try
        {
            var content = await readFile(path, startLine, endLine, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(content) ? null : content;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A file the judge cannot read contributes no excerpt; the other inputs still reach the judge.
            return null;
        }
    }

    // Returns the excerpt of the anchor file outside the anchor window that the judge most likely needs: the
    // declaration of a claimed symbol, or else the first occurrence of a claimed symbol that the window does not show.
    private static JudgeExcerpt? SameFileExcerpt(
        string path,
        string[] lines,
        IReadOnlyList<Regex> patterns,
        int shownStartLine,
        int shownEndLine)
    {
        bool Outside(int line) => line < shownStartLine || line > shownEndLine;

        var index = FindLine(lines, 1, (line, text) => Outside(line) && patterns.Any(pattern => IsDeclarationOf(text, pattern)));
        if (index < 0)
        {
            var missing = patterns
                .Where(pattern => FindLine(lines, 1, (line, text) => !Outside(line) && pattern.IsMatch(text)) < 0)
                .ToList();
            index = missing.Count == 0 ? -1 : FindLine(lines, 1, (line, text) => Outside(line) && missing.Any(pattern => pattern.IsMatch(text)));
        }

        return index < 0 ? null : ExcerptAt(path, lines, 1, index, patterns) with { Origin = JudgeExcerpt.SameFileOrigin };
    }

    // A line declares the symbol when a declaration keyword precedes the symbol on that line, for example
    // "async function name", "def name", "func name", "export const name" or "private void Name(".
    private static bool IsDeclarationOf(string line, Regex pattern)
    {
        var match = pattern.Match(line);
        return match.Success && DeclarationKeywordPattern().IsMatch(line[..match.Index]);
    }

    private static int FindLine(string[] lines, int firstLine, Func<int, string, bool> predicate)
    {
        for (var index = 0; index < lines.Length; index++)
        {
            if (predicate(firstLine + index, lines[index]))
            {
                return index;
            }
        }

        return -1;
    }

    // Returns the excerpt centred on the first line, among the lines the filter accepts, that contains a claimed
    // symbol, or null when no such line exists.
    private static JudgeExcerpt? ExcerptAroundFirstMatch(
        string path,
        string content,
        int firstLine,
        IReadOnlyList<Regex> patterns,
        Func<int, bool> acceptLine)
    {
        var lines = SplitLines(content);
        var matchIndex = FindLine(lines, firstLine, (line, text) => acceptLine(line) && patterns.Any(pattern => pattern.IsMatch(text)));
        return matchIndex < 0 ? null : ExcerptAt(path, lines, firstLine, matchIndex, patterns);
    }

    // Keeps the line at matchIndex and grows the excerpt by whole lines on both sides, one line per side at a time,
    // while it holds at most MaxExcerptLines lines and MaxExcerptChars characters. Each side where lines were left out
    // is marked. A matched line longer than the character budget is cut to a window centred on the first claimed
    // symbol in it, with a truncation marker on each cut side, so the symbol stays in the excerpt.
    private static JudgeExcerpt ExcerptAt(string path, string[] lines, int firstLine, int matchIndex, IReadOnlyList<Regex> patterns)
    {
        var matchText = lines[matchIndex].Length > MaxExcerptChars
            ? CutAroundSymbol(lines[matchIndex], patterns)
            : lines[matchIndex];
        var first = matchIndex;
        var last = matchIndex;
        var used = matchText.Length;
        var grew = true;
        while (grew && last - first + 1 < MaxExcerptLines)
        {
            grew = false;
            if (first > 0 && used + lines[first - 1].Length + 1 <= MaxExcerptChars)
            {
                first--;
                used += lines[first].Length + 1;
                grew = true;
            }

            if (last - first + 1 < MaxExcerptLines && last < lines.Length - 1 && used + lines[last + 1].Length + 1 <= MaxExcerptChars)
            {
                last++;
                used += lines[last].Length + 1;
                grew = true;
            }
        }

        var kept = new List<string>();
        if (first > 0)
        {
            kept.Add(EvidenceJudgeInput.TruncationMarker);
        }

        for (var index = first; index <= last; index++)
        {
            kept.Add(index == matchIndex ? matchText : lines[index]);
        }

        if (last < lines.Length - 1)
        {
            kept.Add(EvidenceJudgeInput.TruncationMarker);
        }

        return new JudgeExcerpt(path, firstLine + first, firstLine + last, string.Join('\n', kept), JudgeExcerpt.ReviewerReadOrigin);
    }

    private static string CutAroundSymbol(string line, IReadOnlyList<Regex> patterns)
    {
        var position = patterns
            .Select(pattern => pattern.Match(line))
            .Where(match => match.Success)
            .Select(match => match.Index + (match.Length / 2))
            .DefaultIfEmpty(0)
            .Min();
        var start = Math.Clamp(position - (MaxExcerptChars / 2), 0, line.Length - MaxExcerptChars);
        var end = start + MaxExcerptChars;
        return string.Concat(
            start > 0 ? EvidenceJudgeInput.TruncationMarker : string.Empty,
            line.AsSpan(start, MaxExcerptChars),
            end < line.Length ? EvidenceJudgeInput.TruncationMarker : string.Empty);
    }

    private static bool Overlaps(JudgeExcerpt first, JudgeExcerpt second)
    {
        return string.Equals(NormalizePath(first.Path), NormalizePath(second.Path), StringComparison.Ordinal)
               && first.StartLine <= second.EndLine
               && second.StartLine <= first.EndLine;
    }

    private static string? NormalizePath(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? null : ReviewDiffProcessor.NormalizeReviewPath(path.Replace('\\', '/'));
    }

    private static string[] SplitLines(string content)
    {
        // A final line break ends the last line and does not start an empty one.
        return content.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
    }

    [GeneratedRegex("`(?<code>[^`\n]{1,200})`", RegexOptions.CultureInvariant)]
    private static partial Regex CodeSpanPattern();

    [GeneratedRegex("[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(
        @"\b(function|def|func|fn|class|interface|struct|enum|type|record|const|let|var|val|void|public|private|protected|internal|static|async|export|override)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex DeclarationKeywordPattern();
}

/// <summary>A bounded excerpt of source for the evidence judge.</summary>
/// <param name="Path">The file the excerpt comes from.</param>
/// <param name="StartLine">The file line of the first excerpt line.</param>
/// <param name="EndLine">The file line of the last excerpt line.</param>
/// <param name="Text">The excerpt, with a truncation marker on each side where lines were left out.</param>
/// <param name="Origin">
///     Where the excerpt comes from: a reviewer read, the anchor file outside the anchor window, or a file the finding
///     cites.
/// </param>
internal sealed record JudgeExcerpt(string Path, int StartLine, int EndLine, string Text, string Origin)
{
    /// <summary>Origin of an excerpt taken from a range the reviewing pass read.</summary>
    internal const string ReviewerReadOrigin = "reviewer_read";

    /// <summary>Origin of an excerpt taken from the anchor file outside the anchor window.</summary>
    internal const string SameFileOrigin = "same_file";

    /// <summary>Origin of an excerpt of a file the finding cites, for a claim without an anchor file.</summary>
    internal const string SupportingFileOrigin = "supporting_file";
}
