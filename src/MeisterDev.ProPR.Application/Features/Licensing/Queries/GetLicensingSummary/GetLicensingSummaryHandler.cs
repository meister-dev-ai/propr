// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Dtos;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;

namespace MeisterDev.ProPR.Application.Features.Licensing.Queries.GetLicensingSummary;

/// <summary>
///     Loads the installation-wide licensing summary for the administration surface.
///     <para>
///         The effective ceiling and the count next to each limit are added here rather than in the capability
///         service, because the same summary is served to sign-in and session callers that never read them.
///         The ceilings also cannot be resolved there: the resolver consults the capability service, so asking
///         it from inside that service would be a cycle.
///     </para>
///     <para>
///         The ceilings come from the resolver every enforcement point reads, so this read reports the numbers
///         a refusal quotes. The counts come from the count source those points admit against.
///     </para>
///     <para>
///         The authors dimension is counted from the rollup instead, and it also reports how many identities
///         the exclusion rules kept out of the current month. Both numbers come from one read of one month, so
///         the count and the identities left out of it agree. A host without a rollup reports neither rather
///         than reporting zero for both.
///     </para>
///     <para>
///         The same read evaluates the author allowance, so the reported overage is current between the
///         lifecycle sweeps that evaluate it on their own cadence. The count this read already took is handed to
///         the evaluation rather than counted again. The trailing-year peak is one further read of the same
///         table.
///     </para>
///     <para>
///         One resolver call per quota. Each reads the license state through its cache rather than the
///         database, and the concurrent-review key additionally consults the capability service, whose read of
///         the installation's capability overrides costs about three database round trips beyond the counts.
///     </para>
///     <para>
///         The ceilings are resolved one quota at a time against that cached state. A turnover of the cache
///         between two of the calls can therefore put ceilings resolved from two states into one payload,
///         which is why each limit carries the source it was resolved from rather than the payload carrying
///         one for all four.
///     </para>
/// </summary>
public sealed class GetLicensingSummaryHandler(
    ILicensingCapabilityService licensingCapabilityService,
    ILicensedResourceCountSource resourceCountSource,
    ILicenseLimitResolver limitResolver,
    IAuthorActivityRollupStore? authorActivityRollup = null,
    IAuthorOverageEvaluator? authorOverageEvaluator = null)
{
    /// <summary>
    ///     Returns the current installation licensing summary, with the effective ceiling and the current
    ///     count for each of its limits.
    /// </summary>
    /// <param name="query">The request.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The summary.</returns>
    public async Task<LicensingSummaryDto> HandleAsync(
        GetLicensingSummaryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var summary = await licensingCapabilityService.GetSummaryAsync(cancellationToken).ConfigureAwait(false);

        if (summary.Limits is not { Count: > 0 } limits)
        {
            return summary;
        }

        var counts = await resourceCountSource.GetCountsAsync(cancellationToken).ConfigureAwait(false);
        var authors = await this.ReadAuthorCountsAsync(cancellationToken).ConfigureAwait(false);
        var reported = new List<LicenseLimitDto>(limits.Count);

        // Sequential rather than gathered, because the resolver reaches the request's database context through
        // the capability service, and that context serves one operation at a time.
        foreach (var limit in limits)
        {
            var effective = await limitResolver.ResolveAsync(limit.Key, cancellationToken).ConfigureAwait(false);

            reported.Add(
                limit with
                {
                    InformationalCount = CountFor(limit.Key, counts, authors),
                    EffectiveCeiling = effective.Ceiling,
                    EffectiveCount = effective.Count,
                    EffectiveSource = effective.Source,
                    ExcludedAutomationCount =
                    limit.Key == LicenseLimitKey.AuthorsPerMonth ? authors?.Excluded : null,
                });
        }

        return summary with
        {
            Limits = reported.AsReadOnly(),
            AuthorOverage = await this.EvaluateOverageAsync(authors, cancellationToken).ConfigureAwait(false),
            AuthorPeakMonth = await this.ReadPeakMonthAsync(cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>
    ///     Evaluates the author allowance and reports where the month stands, or <see langword="null" /> on a
    ///     host with no evaluation to run and when the license states no number to compare against.
    /// </summary>
    /// <remarks>
    ///     Run on the read as well as on the lifecycle sweep, so the panel reports the month as it is now rather
    ///     than as the last sweep left it. Both call points reach the same evaluation, so neither records
    ///     anything the other would not have.
    /// </remarks>
    private async Task<AuthorOverageDto?> EvaluateOverageAsync(
        AuthorCounts? observedAuthors,
        CancellationToken cancellationToken)
    {
        if (authorOverageEvaluator is null)
        {
            return null;
        }

        // The month the count was read for is handed over with it. The record the evaluation writes is keyed
        // by month, so a read that crossed midnight UTC would otherwise attribute this month's count to the
        // next one.
        var overage = await authorOverageEvaluator
            .EvaluateAsync(
                observedAuthors is { } counted ? new AuthorMonthCount(counted.Month, counted.Counted) : null,
                cancellationToken)
            .ConfigureAwait(false);

        return overage is null
            ? null
            : new AuthorOverageDto(overage.LicensedCount, overage.ObservedCount, overage.IsInOverage);
    }

    /// <summary>
    ///     The busiest of the twelve months ending with the current one, or <see langword="null" /> when the
    ///     window holds no counted author and on a host with no rollup to read.
    /// </summary>
    private async Task<AuthorPeakMonthDto?> ReadPeakMonthAsync(CancellationToken cancellationToken)
    {
        if (authorActivityRollup is null)
        {
            return null;
        }

        var peak = await authorActivityRollup
            .GetTrailingYearPeakAsync(cancellationToken)
            .ConfigureAwait(false);

        return peak is null ? null : new AuthorPeakMonthDto(peak.Month, peak.AuthorCount);
    }

    /// <summary>
    ///     The current month's counted authors and the identities the exclusion rules kept out of it, or
    ///     <see langword="null" /> on a host with no rollup to read.
    /// </summary>
    /// <remarks>
    ///     Read once for the whole payload rather than per limit, because both numbers belong to the authors
    ///     dimension. They are taken from the same table in the same request, so the count and the identities
    ///     left out of it describe one month.
    /// </remarks>
    private async Task<AuthorCounts?> ReadAuthorCountsAsync(CancellationToken cancellationToken)
    {
        if (authorActivityRollup is null)
        {
            return null;
        }

        var month = await authorActivityRollup.GetCurrentMonthCountsAsync(cancellationToken).ConfigureAwait(false);
        return new AuthorCounts(month.Month, month.Counted, month.Excluded);
    }

    /// <summary>
    ///     The count for one dimension, or <see langword="null" /> for a dimension this host cannot measure.
    ///     Authors come from the rollup rather than from the count source, so a host without one reports no
    ///     number for them rather than reporting zero.
    /// </summary>
    private static long? CountFor(LicenseLimitKey key, LicensedResourceCounts counts, AuthorCounts? authors)
    {
        return key switch
        {
            LicenseLimitKey.Clients => counts.Clients,
            LicenseLimitKey.Runners => counts.EnrolledRunners,
            LicenseLimitKey.ConcurrentReviews => counts.ReviewsInProgress,
            LicenseLimitKey.AuthorsPerMonth => authors?.Counted,
            _ => null,
        };
    }

    /// <summary>
    ///     What the current month holds for the authors dimension: the counted authors, the excluded ones, and
    ///     the month both were read for.
    /// </summary>
    private readonly record struct AuthorCounts(DateOnly Month, long Counted, long Excluded);
}
