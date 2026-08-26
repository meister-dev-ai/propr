// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Licensing.Services;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Licensing;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Tests.Features.Licensing;

/// <summary>
///     The comparison between the month's counted authors and the number the license states for them. The
///     comparison reports and records; nothing it produces decides whether work runs.
/// </summary>
public sealed class AuthorOverageEvaluatorTests
{
    private const long LicensedNumber = 5;

    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The month the rollup reports unless a test moves it.</summary>
    private static readonly DateOnly CurrentMonth = new(2026, 8, 1);

    /// <summary>
    ///     The license states a number and the month is above it, which is the one case that records.
    /// </summary>
    [Fact]
    public async Task ACountAboveTheNumber_RecordsTheMonthAndReportsTheOverage()
    {
        var harness = new Harness(StateWith(LicenseLimit.Of(LicensedNumber)), observedCount: 8);

        var overage = await harness.Evaluator.EvaluateAsync();

        Assert.NotNull(overage);
        Assert.True(overage.IsInOverage);
        Assert.Equal(LicensedNumber, overage.LicensedCount);
        Assert.Equal(8, overage.ObservedCount);
        Assert.Equal([(CurrentMonth, LicensedNumber, 8L)], harness.Store.Writes);
    }

    // The count has to pass the number, not reach it. A month holding exactly the licensed authors is licensed
    // use, so recording it would report an installation that is inside what it paid for.
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task ACountAtOrBelowTheNumber_ReportsNoOverageAndRecordsNothing(int observedCount)
    {
        var harness = new Harness(StateWith(LicenseLimit.Of(LicensedNumber)), observedCount);

        var overage = await harness.Evaluator.EvaluateAsync();

        Assert.NotNull(overage);
        Assert.False(overage.IsInOverage);
        Assert.Equal(observedCount, overage.ObservedCount);
        Assert.Empty(harness.Store.Writes);
    }

    // A month that returns to at or below the number is reported as no longer above it, while the row the earlier
    // observation wrote stays where it is. The report comes from the live comparison, so it clears without
    // anything deleting history.
    [Fact]
    public async Task AMonthBackBelowTheNumber_ReportsNoOverageWhileItsRecordRemains()
    {
        var harness = new Harness(StateWith(LicenseLimit.Of(LicensedNumber)), observedCount: 9);

        var above = await harness.Evaluator.EvaluateAsync();

        harness.Rollup.CurrentMonthAuthors = 3;
        var backBelow = await harness.Evaluator.EvaluateAsync();

        Assert.True(above!.IsInOverage);
        Assert.False(backBelow!.IsInOverage);
        Assert.Equal([(CurrentMonth, LicensedNumber, 9L)], harness.Store.Writes);
        Assert.Single(harness.Store.Months);
    }

    // Three readings the license can carry for this dimension have no number to compare against. Recording
    // against a number nobody stated would report an overage the document does not support.
    [Fact]
    public async Task ALicenseThatLeavesTheAuthorLimitOut_EvaluatesNothing()
    {
        var harness = new Harness(StateWith(LicenseLimit.Absent), observedCount: 40);

        Assert.Null(await harness.Evaluator.EvaluateAsync());
        Assert.Empty(harness.Store.Writes);
    }

    [Fact]
    public async Task ALicenseThatStatesTheAuthorLimitAsUnlimited_EvaluatesNothing()
    {
        var harness = new Harness(StateWith(LicenseLimit.Unlimited), observedCount: 40);

        Assert.Null(await harness.Evaluator.EvaluateAsync());
        Assert.Empty(harness.Store.Writes);
    }

    [Fact]
    public async Task WithoutALicenseOnFile_EvaluatesNothing()
    {
        var harness = new Harness(LicenseState.None(), observedCount: 40);

        Assert.Null(await harness.Evaluator.EvaluateAsync());
        Assert.Empty(harness.Store.Writes);
    }

    // A license outside the stages the rest of licensing treats as commercial states a number that does not
    // apply, so there is nothing to compare the month against.
    [Theory]
    [InlineData(LicenseStage.NotYetValid)]
    [InlineData(LicenseStage.Reverted)]
    public async Task ALicenseNotInForce_EvaluatesNothing(LicenseStage stage)
    {
        var harness = new Harness(StateWith(LicenseLimit.Of(LicensedNumber), stage), observedCount: 40);

        Assert.Null(await harness.Evaluator.EvaluateAsync());
        Assert.Empty(harness.Store.Writes);
    }

    [Fact]
    public async Task AStoredDocumentThatDidNotVerify_EvaluatesNothing()
    {
        var harness = new Harness(
            LicenseState.Invalid(LicenseFailureReason.UntrustedSigner, "not our signer", Now, null),
            observedCount: 40);

        Assert.Null(await harness.Evaluator.EvaluateAsync());
        Assert.Empty(harness.Store.Writes);
    }

    // The stages a license is still in force in include the two an operator has to act on. An expiry does not
    // stop the allowance being metered before the grace window has closed as well.
    [Theory]
    [InlineData(LicenseStage.Active)]
    [InlineData(LicenseStage.Warning)]
    [InlineData(LicenseStage.Grace)]
    public async Task ALicenseInForce_IsEvaluatedInEveryStageItIsInForceIn(LicenseStage stage)
    {
        var harness = new Harness(StateWith(LicenseLimit.Of(LicensedNumber), stage), observedCount: 8);

        var overage = await harness.Evaluator.EvaluateAsync();

        Assert.True(overage!.IsInOverage);
        Assert.Equal([(CurrentMonth, LicensedNumber, 8L)], harness.Store.Writes);
    }

    // The evaluation runs on every lifecycle sweep and on every administration read, and the month it reports
    // holds for the rest of the month. The insert is the edge, so the line an operator reads is written once.
    [Fact]
    public async Task TheFirstObservationOfAMonth_IsReportedOnce()
    {
        var harness = new Harness(StateWith(LicenseLimit.Of(LicensedNumber)), observedCount: 8);

        await harness.Evaluator.EvaluateAsync();
        await harness.Evaluator.EvaluateAsync();
        await harness.Evaluator.EvaluateAsync();

        var entry = Assert.Single(harness.Log.Entries);
        Assert.Equal(LogLevel.Warning, entry.LogLevel);
        Assert.Contains("8", entry.Message, StringComparison.Ordinal);
        Assert.Contains("5", entry.Message, StringComparison.Ordinal);
    }

    // Every user-facing line about the allowance has to state that the installation kept running, because the
    // allowance is metered and an operator reading a warning would otherwise look for what stopped.
    [Fact]
    public async Task TheReportedLine_StatesThatNothingHasBeenWithheld()
    {
        var harness = new Harness(StateWith(LicenseLimit.Of(LicensedNumber)), observedCount: 8);

        await harness.Evaluator.EvaluateAsync();

        Assert.Contains(
            "Nothing has been withheld, delayed or degraded",
            Assert.Single(harness.Log.Entries).Message,
            StringComparison.Ordinal);
    }

    // The administration read has already counted the month, and the evaluation it triggers must not count it
    // again for one request.
    [Fact]
    public async Task ACountTheCallerAlreadyHas_IsUsedInsteadOfCountingTheMonthAgain()
    {
        var harness = new Harness(StateWith(LicenseLimit.Of(LicensedNumber)), observedCount: 2);

        var overage = await harness.Evaluator.EvaluateAsync(new AuthorMonthCount(CurrentMonth, 11));

        Assert.Equal(11, overage!.ObservedCount);
        Assert.True(overage.IsInOverage);
        Assert.Equal(0, harness.Rollup.CountReads);
    }

    // The month the count was taken in decides which row is written. An evaluation that reads a count just
    // before midnight UTC and reaches the store just after would otherwise write August's number into
    // September, where the ratchet on the row makes it permanent.
    [Fact]
    public async Task ACountFromAnEarlierMonth_IsRecordedAgainstTheMonthItWasCountedIn()
    {
        var harness = new Harness(StateWith(LicenseLimit.Of(LicensedNumber)), observedCount: 0);
        harness.Rollup.CurrentMonth = new DateOnly(2026, 9, 1);

        await harness.Evaluator.EvaluateAsync(new AuthorMonthCount(CurrentMonth, 11));

        Assert.Equal([(CurrentMonth, LicensedNumber, 11L)], harness.Store.Writes);
    }

    // The count and the month it was taken for come from one read when the caller supplies neither, so they
    // cannot describe two different months.
    [Fact]
    public async Task ACountReadHere_IsRecordedAgainstTheMonthThatReadReported()
    {
        var harness = new Harness(StateWith(LicenseLimit.Of(LicensedNumber)), observedCount: 8);
        harness.Rollup.CurrentMonth = new DateOnly(2026, 9, 1);

        await harness.Evaluator.EvaluateAsync();

        Assert.Equal([(new DateOnly(2026, 9, 1), LicensedNumber, 8L)], harness.Store.Writes);
    }

    // The record decides nothing, so a write that fails is reported and left for the next evaluation. What the
    // caller was told about the month stands either way.
    [Fact]
    public async Task AFailedRecord_IsReportedAndLeavesTheOverageStanding()
    {
        var harness = new Harness(
            StateWith(LicenseLimit.Of(LicensedNumber)),
            observedCount: 8,
            new ThrowingAuthorOverageStore());

        var overage = await harness.Evaluator.EvaluateAsync();

        Assert.True(overage!.IsInOverage);
        Assert.Equal(8, overage.ObservedCount);

        var entry = Assert.Single(harness.Log.Entries);
        Assert.Equal(LogLevel.Warning, entry.LogLevel);
        Assert.Contains("could not be recorded", entry.Message, StringComparison.Ordinal);
    }

    private static LicenseState StateWith(LicenseLimit authorsPerMonth, LicenseStage stage = LicenseStage.Active) =>
        new()
        {
            Kind = LicenseStateKind.Verified,
            Stage = stage,
            Claims = new LicenseClaims
            {
                LicenseId = "b7c1d2e3",
                Licensee = "Northwind Traders",
                IssuedAt = Now.AddDays(-30),
                NotBefore = Now.AddDays(-30),
                ExpiresAt = Now.AddYears(1),
                Capabilities = [],
                Limits = new LicenseLimits { AuthorsPerMonth = authorsPerMonth },
            },
        };

    private sealed class Harness
    {
        public Harness(LicenseState licenseState, int observedCount, IAuthorOverageStore? store = null)
        {
            this.Rollup = new StubRollupStore { CurrentMonthAuthors = observedCount };
            this.Evaluator = new AuthorOverageEvaluator(
                new FixedLicenseStateProvider(licenseState),
                this.Rollup,
                store ?? this.Store,
                this.Log);
        }

        public AuthorOverageEvaluator Evaluator { get; }

        public ListLogger Log { get; } = new();

        public StubRollupStore Rollup { get; }

        /// <summary>The store the evaluator writes to unless the test supplied its own.</summary>
        public RecordingAuthorOverageStore Store { get; } = new();
    }

    /// <summary>Keeps the months it was asked to record and what each write carried.</summary>
    private sealed class RecordingAuthorOverageStore : IAuthorOverageStore
    {
        private readonly HashSet<DateOnly> _months = [];

        public List<(DateOnly Month, long LicensedCount, long ObservedCount)> Writes { get; } = [];

        /// <summary>The distinct months the store holds a row for.</summary>
        public IReadOnlyCollection<DateOnly> Months => this._months;

        public Task<bool> RecordAsync(
            DateOnly month,
            long licensedCount,
            long observedCount,
            CancellationToken cancellationToken = default)
        {
            this.Writes.Add((month, licensedCount, observedCount));

            // The first write of a month inserts and every later one finds the row, the same answer the
            // database gives.
            return Task.FromResult(this._months.Add(month));
        }
    }

    private sealed class ThrowingAuthorOverageStore : IAuthorOverageStore
    {
        public Task<bool> RecordAsync(
            DateOnly month,
            long licensedCount,
            long observedCount,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the overage record is unreachable");
    }

    /// <summary>Reports one count for the current month, counting how often it was asked.</summary>
    private sealed class StubRollupStore : IAuthorActivityRollupStore
    {
        public int CountReads { get; private set; }

        public int CurrentMonthAuthors { get; set; }

        public DateOnly CurrentMonth { get; set; } = new(2026, 8, 1);

        public Task RecordAuthorAsync(
            ProviderHostRef host,
            string externalUserId,
            AuthorActivitySource source,
            bool excluded,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> CountCurrentMonthAuthorsAsync(CancellationToken cancellationToken = default)
        {
            this.CountReads++;

            return Task.FromResult(this.CurrentMonthAuthors);
        }

        public Task<int> CountCurrentMonthExcludedAuthorsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<AuthorActivityMonthCounts> GetCurrentMonthCountsAsync(CancellationToken cancellationToken = default)
        {
            this.CountReads++;

            return Task.FromResult(new AuthorActivityMonthCounts(this.CurrentMonth, this.CurrentMonthAuthors, 0));
        }

        public Task<AuthorMonthCount?> GetTrailingYearPeakAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<AuthorMonthCount?>(null);
    }

    private sealed class FixedLicenseStateProvider(LicenseState state) : ILicenseStateProvider
    {
        public Task<LicenseState> GetStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(state);

        public void Invalidate()
        {
        }
    }

    /// <summary>Keeps what was logged, which is what the once-per-month rule is asserted against.</summary>
    private sealed class ListLogger : ILogger<AuthorOverageEvaluator>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            this.Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }

        public sealed record LogEntry(LogLevel LogLevel, string Message);
    }
}
