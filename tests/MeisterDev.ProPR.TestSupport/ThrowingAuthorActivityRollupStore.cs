// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.TestSupport;

/// <summary>
///     A rollup store that refuses every call.
///     <para>
///         The completion paths record an author fail-soft, and a store that throws is what shows the
///         completion is unaffected. Both hook sites need one, so it lives here rather than once per test class.
///     </para>
/// </summary>
public sealed class ThrowingAuthorActivityRollupStore : IAuthorActivityRollupStore
{
    private const string Refusal = "The rollup is unreachable.";

    /// <inheritdoc />
    public Task RecordAuthorAsync(
        ProviderHostRef host,
        string externalUserId,
        AuthorActivitySource source,
        bool excluded,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(Refusal);
    }

    /// <inheritdoc />
    public Task<int> CountCurrentMonthAuthorsAsync(CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(Refusal);
    }

    /// <inheritdoc />
    public Task<int> CountCurrentMonthExcludedAuthorsAsync(CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(Refusal);
    }

    /// <inheritdoc />
    public Task<AuthorActivityMonthCounts> GetCurrentMonthCountsAsync(CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(Refusal);
    }

    /// <inheritdoc />
    public Task<AuthorMonthCount?> GetTrailingYearPeakAsync(CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(Refusal);
    }
}
