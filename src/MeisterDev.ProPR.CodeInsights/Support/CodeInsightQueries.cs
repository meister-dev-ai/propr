// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.CodeInsights.Metrics;
using MeisterDev.ProPR.CodeInsights.Rollups;
using MeisterDev.ProPR.CodeInsights.Http;

namespace MeisterDev.ProPR.CodeInsights.Support;

/// <summary>
///     Turns query-string input into the reader contracts, and reader results into responses.
/// </summary>
/// <remarks>
///     Shared by both audiences' controllers so a window, a bucket, or a limit cannot come to mean two different
///     things depending on which view asked. Every parser is forgiving in the same direction: an unrecognised value
///     falls back to a sensible default rather than failing a read, because a chart that will not load teaches an
///     operator nothing about what they typed.
/// </remarks>
public static class CodeInsightQueries
{
    /// <summary>Window applied when the caller names neither end.</summary>
    public const int DefaultWindowDays = 30;

    public static CodeInsightRollupQuery BuildRollupQuery(
        IReadOnlyList<Guid> clientIds,
        DateOnly? from,
        DateOnly? to,
        string? repositoryId = null,
        long? pullRequestId = null,
        string? filePath = null)
    {
        var (start, end) = ResolveWindow(from, to);
        return new CodeInsightRollupQuery(
            clientIds,
            start,
            end,
            Trimmed(repositoryId),
            pullRequestId,
            Trimmed(filePath));
    }

    public static CodeInsightBrowseQuery BuildBrowseQuery(
        IReadOnlyList<Guid> clientIds,
        DateOnly? from,
        DateOnly? to,
        string? repositoryId,
        long? pullRequestId,
        string? filePath,
        string? coreType,
        CodeInsightDisposition? disposition,
        int limit,
        string? symbolName = null,
        CodeInsightRejectionReason? rejectionReason = null)
    {
        var (start, end) = ResolveWindow(from, to);
        return new CodeInsightBrowseQuery(
            clientIds,
            start,
            end,
            Trimmed(repositoryId),
            pullRequestId,
            Trimmed(filePath),
            Trimmed(coreType),
            disposition,
            Math.Clamp(limit, 1, 200),
            Trimmed(symbolName),
            rejectionReason);
    }

    /// <summary>
    ///     Resolves the window, defaulting to the last thirty days and repairing a reversed range rather than
    ///     returning nothing for it.
    /// </summary>
    public static (DateOnly From, DateOnly To) ResolveWindow(DateOnly? from, DateOnly? to)
    {
        var end = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var start = from ?? end.AddDays(-DefaultWindowDays);
        return start <= end ? (start, end) : (end, start);
    }

    public static CodeInsightBucketSize ParseBucket(
        string? bucket,
        CodeInsightBucketSize fallback = CodeInsightBucketSize.Day)
    {
        return Enum.TryParse<CodeInsightBucketSize>(bucket, ignoreCase: true, out var parsed) ? parsed : fallback;
    }

    public static CodeInsightGrain ParseGrain(string? grain)
    {
        return Enum.TryParse<CodeInsightGrain>(grain, ignoreCase: true, out var parsed)
            ? parsed
            : CodeInsightGrain.Repository;
    }

    /// <summary>
    ///     Whether the caller asked to group by producing model. Deliberately not a
    ///     <see cref="CodeInsightGrain" /> member: every grain there is a prefix of the count projection's key, and
    ///     the model is not part of that key: it is a property of what produced a finding rather than of where the
    ///     finding landed.
    /// </summary>
    public static bool IsModelGrain(string? grain)
    {
        return string.Equals(Trimmed(grain), "model", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Whether a hotspot ranking groups by file or by the definition inside it. Forgiving like every parser
    ///     here: anything unrecognised is the file grouping, which is the one that can always be computed.
    /// </summary>
    public static CodeInsightHotspotGrouping ParseHotspotGrouping(string? groupBy)
    {
        return string.Equals(Trimmed(groupBy), "symbol", StringComparison.OrdinalIgnoreCase)
            ? CodeInsightHotspotGrouping.Symbol
            : CodeInsightHotspotGrouping.File;
    }

    public static CodeInsightDisposition? ParseDisposition(string? disposition)
    {
        return Enum.TryParse<CodeInsightDisposition>(disposition, ignoreCase: true, out var parsed) ? parsed : null;
    }

    /// <summary>
    ///     Parses a rejection reason, returning <see langword="null" /> for anything unrecognised. An unknown
    ///     value narrows nothing rather than erroring, matching how every other optional narrowing here behaves.
    /// </summary>
    /// <remarks>
    ///     Separators are stripped first, so <c>outOfScope</c>, <c>OutOfScope</c> and <c>out_of_scope</c> all
    ///     resolve. The reason has two vocabularies with a good reason for each: the classifier prompt names
    ///     them in snake case for a model to echo, and the wire carries the enum name like every other outcome
    ///     field here. A caller that mixes them up should get the narrowing it asked for rather than silence.
    /// </remarks>
    public static CodeInsightRejectionReason? ParseRejectionReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        var normalized = reason.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim();

        return Enum.TryParse<CodeInsightRejectionReason>(normalized, ignoreCase: true, out var parsed)
            ? parsed
            : null;
    }

    public static string? Trimmed(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static CodeInsightMetricPointResponse ToPoint(CodeInsightMetricSeriesPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        return new CodeInsightMetricPointResponse(point.BucketStart, ToMetric(point.Result));
    }

    public static CodeInsightMetricResponse ToMetric(CodeInsightMetricResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var inputs = result.Metrics.Inputs;
        return new CodeInsightMetricResponse(
            result.Metrics.Precision,
            result.Metrics.Recall,
            result.Metrics.F1,
            result.Metrics.AcceptanceRate,
            inputs.Addressed,
            inputs.Acknowledged,
            inputs.Dismissed,
            inputs.FalsePositive,
            inputs.Misses,
            result.SampleSize,
            inputs.Discussed);
    }
}
