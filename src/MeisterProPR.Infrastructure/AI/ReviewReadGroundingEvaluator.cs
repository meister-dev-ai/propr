// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Services;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     Classifies whether the reviewer read the actual source at a finding's cited line, from the structured
///     file-read records captured during the review pass. This is a grounding signal, not a correctness one:
///     a covering read proves the reviewer had the cited source in front of it, not that its conclusion is right.
///     The classifier is a pure comparison over typed records — the reads are measured once, at the point the
///     tool ran (<see cref="CreateReadRecord" />), so there is no re-parsing of serialized tool arguments and no
///     scraping of the line-annotated, character-bounded text the model was shown.
/// </summary>
public static class ReviewReadGroundingEvaluator
{
    // Mirror the sentinel results the file-content tool returns in place of real source. Matched against the RAW
    // tool return (before annotation/bounding), where the value is unambiguous.
    private const string BinaryFileSentinel = "[Binary file";
    private const string FileTooLargeSentinel = "[File too large";

    /// <summary>
    ///     Measures a single <c>get_file_content</c> invocation into a typed record, computed from the raw returned
    ///     content before it is line-annotated or bounded for replay. This is the only place the read is
    ///     interpreted; the classifier then works purely from the record.
    /// </summary>
    /// <param name="path">The file path the model requested.</param>
    /// <param name="startLine">The first line of the requested window (as supplied by the model).</param>
    /// <param name="endLine">The last line of the requested window (as supplied by the model).</param>
    /// <param name="rawContent">The tool's raw return value, before annotation/bounding.</param>
    public static FileReadRecord CreateReadRecord(string path, int startLine, int endLine, string? rawContent)
    {
        ArgumentNullException.ThrowIfNull(path);

        var windowStart = startLine <= 0 ? 1 : startLine;
        var normalizedPath = NormalizePathForMatch(path);

        if (string.IsNullOrWhiteSpace(rawContent) || IsUnavailableSentinel(rawContent))
        {
            // Empty (missing file / range beyond end of file / transient failure) or an unavailable placeholder
            // (binary / too large): the reviewer saw no real source, so the read grounds nothing.
            return new FileReadRecord(normalizedPath, windowStart, endLine, HasContent: false, LastLinePresent: 0);
        }

        return new FileReadRecord(normalizedPath, windowStart, endLine, HasContent: true, windowStart + CountContentLines(rawContent) - 1);
    }

    /// <summary>
    ///     Classifies read-grounding for a comment anchored at <paramref name="filePath" />:<paramref name="lineNumber" />
    ///     against the reads captured for the pass that produced it.
    /// </summary>
    /// <returns>
    ///     <see langword="null" /> when grounding does not apply (no path or line). Otherwise
    ///     <see cref="ReviewCommentReadGrounding.Covered" />, <see cref="ReviewCommentReadGrounding.NotRead" />, or
    ///     <see cref="ReviewCommentReadGrounding.CitedLineMissing" />. Presence wins over provable absence, which
    ///     wins over "not read"; anything ambiguous never yields provable absence, so a genuine finding is never
    ///     discarded on uncertainty.
    /// </returns>
    public static ReviewCommentReadGrounding? Classify(string? filePath, int? lineNumber, IReadOnlyList<FileReadRecord> reads)
    {
        ArgumentNullException.ThrowIfNull(reads);

        if (string.IsNullOrWhiteSpace(filePath) || lineNumber is not int line || line <= 0)
        {
            return null;
        }

        var target = NormalizePathForMatch(filePath);
        var sawProvableAbsence = false;

        foreach (var read in reads)
        {
            // Covering means the reviewer's requested window for this file included the cited line.
            if (!string.Equals(read.NormalizedPath, target, StringComparison.OrdinalIgnoreCase)
                || read.EndLine < read.StartLine
                || line < read.StartLine
                || line > read.EndLine)
            {
                continue;
            }

            if (!read.HasContent)
            {
                // Ambiguous read (empty / unavailable) — establishes neither presence nor absence.
                continue;
            }

            if (line <= read.LastLinePresent)
            {
                return ReviewCommentReadGrounding.Covered;
            }

            // The requested window included the cited line, but the file's content ended before it: the line is
            // provably beyond end of file.
            sawProvableAbsence = true;
        }

        return sawProvableAbsence ? ReviewCommentReadGrounding.CitedLineMissing : ReviewCommentReadGrounding.NotRead;
    }

    private static bool IsUnavailableSentinel(string content)
    {
        return content.StartsWith(BinaryFileSentinel, StringComparison.Ordinal)
               || content.StartsWith(FileTooLargeSentinel, StringComparison.Ordinal);
    }

    // Counts physical source lines in raw content, ignoring a single trailing newline (structure, not a line) so
    // the last line number is exact. Only called for non-empty content.
    private static int CountContentLines(string content)
    {
        var newlineCount = 0;
        foreach (var ch in content)
        {
            if (ch == '\n')
            {
                newlineCount++;
            }
        }

        return content[^1] == '\n' ? newlineCount : newlineCount + 1;
    }

    // Provider-comparable relative path: reconcile separators and strip the leading slash so a model-supplied
    // "\src\Foo.cs" or "/src/Foo.cs" matches a finding's "src/Foo.cs".
    private static string NormalizePathForMatch(string path)
    {
        return ReviewDiffProcessor.NormalizeReviewPath(path.Replace('\\', '/'));
    }
}

/// <summary>
///     A file read reduced to the typed facts the grounding classifier needs, measured at the
///     <c>get_file_content</c> call site from the raw returned content.
/// </summary>
/// <param name="NormalizedPath">Provider-comparable relative path the read targeted.</param>
/// <param name="StartLine">First line of the reviewer's requested window (1-based; at least 1).</param>
/// <param name="EndLine">Last line of the reviewer's requested window.</param>
/// <param name="HasContent">Whether the read returned real source (not empty, not an unavailable placeholder).</param>
/// <param name="LastLinePresent">The last file line actually returned when <see cref="HasContent" /> is true; 0 otherwise.</param>
public sealed record FileReadRecord(string NormalizedPath, int StartLine, int EndLine, bool HasContent, int LastLinePresent);
