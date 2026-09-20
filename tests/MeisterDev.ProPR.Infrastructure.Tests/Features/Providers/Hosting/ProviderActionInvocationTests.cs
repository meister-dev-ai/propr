// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;
using MeisterDev.ProPR.TestSupport;
using Microsoft.Extensions.Time.Testing;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers.Hosting;

/// <summary>
///     The record of an operator-started add-in action: the terminal state a family writes against it long after
///     the dispatch returned, the window that closes it when nothing does, and the signal it watches.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class ProviderActionInvocationTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private ProviderHostPrimitiveHarness _harness = null!;

    public Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        this._harness = ProviderHostPrimitiveHarness.Create(fixture.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (fixture.IsAvailable)
        {
            await this._harness.DisposeAsync();
        }
    }

    // An operator signing in at a vendor takes minutes, and the result arrives on something other than the
    // request that started the action. The family writes the terminal state instead, and whatever is watching
    // reads it back.
    [Fact]
    public async Task AFamilyWritesATerminalStateLongAfterTheDispatchReturned()
    {
        var binding = await this._harness.SeedConnectionAsync("Completes later");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var invocations = this.Invocations(clock);

        var invocation = await invocations.OpenAsync(binding, "connect", Guid.NewGuid(), TimeSpan.FromMinutes(10));
        Assert.Equal(ProviderInvocationState.Pending, invocation.State);

        clock.Advance(TimeSpan.FromMinutes(4));
        await this.ReporterFor(invocation.Id, invocations, binding)
            .ReportCompletedAsync("The account is connected.");

        var read = await invocations.GetAsync(invocation.Id);

        Assert.NotNull(read);
        Assert.Equal(ProviderInvocationState.Completed, read.State);
        Assert.Equal("The account is connected.", read.TerminalMessage);
    }

    // A family reports against the invocation the host handed it, which is fixed when the reporter is built. It
    // never names one, so it cannot close another administrator's run.
    [Fact]
    public async Task AFamilyCannotWriteATerminalStateAgainstAnInvocationItWasNotHanded()
    {
        var binding = await this._harness.SeedConnectionAsync("Two invocations");
        var invocations = this.Invocations();

        var mine = await invocations.OpenAsync(binding, "connect", Guid.NewGuid(), TimeSpan.FromMinutes(10));
        var other = await invocations.OpenAsync(binding, "connect", Guid.NewGuid(), TimeSpan.FromMinutes(10));

        await this.ReporterFor(mine.Id, invocations, binding).ReportCompletedAsync("mine finished");

        var readOther = await invocations.GetAsync(other.Id);

        Assert.NotNull(readOther);
        Assert.Equal(ProviderInvocationState.Pending, readOther.State);
        Assert.Null(readOther.TerminalMessage);
    }

    // An invocation that outlives its window is expired rather than left pending, so an operator whose browser
    // never reached the family's listener is told the run is over instead of watching it never resolve.
    [Fact]
    public async Task AnInvocationStillPendingPastItsWindowIsExpired()
    {
        var binding = await this._harness.SeedConnectionAsync("Ran out of window");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var invocations = this.Invocations(clock);

        var invocation = await invocations.OpenAsync(binding, "connect", Guid.NewGuid(), TimeSpan.FromMinutes(10));
        clock.Advance(TimeSpan.FromMinutes(11));

        var read = await invocations.GetAsync(invocation.Id);

        Assert.NotNull(read);
        Assert.Equal(ProviderInvocationState.Expired, read.State);
    }

    // Reporting twice does not overwrite what was said first, and a report that arrives after the window closed
    // does not reopen a run the host has given up on.
    [Fact]
    public async Task AnInvocationThatHasReachedATerminalStateKeepsIt()
    {
        var binding = await this._harness.SeedConnectionAsync("Reports twice");
        var invocations = this.Invocations();
        var invocation = await invocations.OpenAsync(binding, "connect", Guid.NewGuid(), TimeSpan.FromMinutes(10));
        var reporter = this.ReporterFor(invocation.Id, invocations, binding);

        await reporter.ReportCompletedAsync("the first word");
        await reporter.ReportFailedAsync("the second word");

        var read = await invocations.GetAsync(invocation.Id);

        Assert.NotNull(read);
        Assert.Equal(ProviderInvocationState.Completed, read.State);
        Assert.Equal("the first word", read.TerminalMessage);
    }

    // The message is a string a family produced, so the host caps it and scrubs the connection's credential out
    // of it before it reaches a column.
    [Fact]
    public async Task TheMessageAFamilyWritesIsCappedAndScrubbedBeforeItIsStored()
    {
        var binding = await this._harness.SeedConnectionAsync("Echoes the credential");
        await this._harness.SeedCredentialAsync(
            binding,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["accessToken"] = "sk-the-secret-token" },
            DateTimeOffset.UtcNow.AddHours(1));

        var invocations = this.Invocations();
        var invocation = await invocations.OpenAsync(binding, "connect", Guid.NewGuid(), TimeSpan.FromMinutes(10));

        await this.ReporterFor(invocation.Id, invocations, binding).ReportFailedAsync(
            "The provider refused the key sk-the-secret-token. " + new string('x', 4000));

        var read = await invocations.GetAsync(invocation.Id);

        Assert.NotNull(read);
        Assert.NotNull(read.TerminalMessage);
        Assert.DoesNotContain("sk-the-secret-token", read.TerminalMessage, StringComparison.Ordinal);
        Assert.True(read.TerminalMessage.Length <= ProviderHostLimits.MaximumMessageLength);
    }

    // A family that finishes after the host gave up on its run is reporting something true about work nobody is
    // waiting for any more. The report changes nothing, and it is not an error either: failing it would leave
    // the family holding an exception it can do nothing about.
    [Fact]
    public async Task ATerminalStateWrittenAfterTheWindowClosedLeavesTheInvocationExpired()
    {
        var binding = await this._harness.SeedConnectionAsync("Reports after the window");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var invocations = this.Invocations(clock);

        var invocation = await invocations.OpenAsync(
            binding,
            "connect",
            Guid.NewGuid(),
            TimeSpan.FromMinutes(10),
            "Waiting for the provider.");

        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(ProviderInvocationState.Expired, (await invocations.GetAsync(invocation.Id))!.State);

        await this.ReporterFor(invocation.Id, invocations, binding).ReportCompletedAsync("Connected, eventually.");

        var read = await invocations.GetAsync(invocation.Id);

        Assert.NotNull(read);
        Assert.Equal(ProviderInvocationState.Expired, read.State);
        Assert.Equal("Waiting for the provider.", read.TerminalMessage);
    }

    // The signal is what a family watches while its action is in flight. The host trips it when the window
    // closes, and again for every run still open when the process stops.
    [Fact]
    public void TheCancellationSignalIsTrippedAtTheWindowAndForEveryRunStillOpenAtShutdown()
    {
        using var cancellations = new ProviderInvocationCancellation();

        var atTheWindow = cancellations.Open(Guid.NewGuid(), TimeSpan.FromMilliseconds(1));
        var stillOpen = cancellations.Open(Guid.NewGuid(), TimeSpan.FromMinutes(10));

        Assert.True(SpinWait.SpinUntil(() => atTheWindow.IsCancellationRequested, TimeSpan.FromSeconds(5)));
        Assert.False(stillOpen.IsCancellationRequested);

        // Disposal is what the container does while the host stops.
        cancellations.Dispose();

        Assert.True(stillOpen.IsCancellationRequested);
    }

    // A run the host has finished with gives its signal back, so a process serving actions for a long time does
    // not accumulate one per run it has already closed.
    [Fact]
    public void ClosingAnInvocationGivesItsSignalBack()
    {
        using var cancellations = new ProviderInvocationCancellation();
        var invocationId = Guid.NewGuid();

        var token = cancellations.Open(invocationId, TimeSpan.FromMinutes(10));
        cancellations.Close(invocationId);

        cancellations.Dispose();

        Assert.False(token.IsCancellationRequested);
    }

    // The sweep that marks a run expired runs when a run is read, so between the window closing and the next read
    // the row still says pending. Closed on that alone, an answer arriving in that interval reaches a terminal
    // state after the window an operator was told about had already passed.
    [Fact]
    public async Task AnAnswerArrivingAfterTheWindowClosesNothingEvenBeforeTheSweepHasRun()
    {
        var binding = await this._harness.SeedConnectionAsync("Answers inside the gap");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var invocations = this.Invocations(clock);

        var invocation = await invocations.OpenAsync(
            binding,
            "connect",
            Guid.NewGuid(),
            TimeSpan.FromMinutes(10),
            "Waiting for the provider.");

        // The window passes and nothing reads the run, so the row is still pending when the answer arrives.
        clock.Advance(TimeSpan.FromMinutes(11));

        var closed = await invocations.TryCloseAsync(
            invocation.Id,
            ProviderInvocationState.Completed,
            "Connected, eventually.");

        Assert.False(closed);

        var read = await invocations.GetAsync(invocation.Id);

        Assert.NotNull(read);
        Assert.Equal(ProviderInvocationState.Expired, read.State);
        Assert.Equal("Waiting for the provider.", read.TerminalMessage);
    }

    // A signal that trips on its own timer is finished with, and nothing closes the run it belonged to: such a
    // run is expired by the sweep rather than by a family reporting. Left registered, the host holds a source per
    // action that ran out of its window and names those runs as still open.
    [Fact]
    public void ASignalThatTripsAtItsOwnWindowIsNoLongerReportedAsLive()
    {
        using var cancellations = new ProviderInvocationCancellation();
        var ranOut = Guid.NewGuid();

        var token = cancellations.Open(ranOut, TimeSpan.FromMilliseconds(1));
        Assert.True(SpinWait.SpinUntil(() => token.IsCancellationRequested, TimeSpan.FromSeconds(5)));

        Assert.DoesNotContain(ranOut, cancellations.Live);
    }

    private ProviderActionInvocations Invocations(TimeProvider? timeProvider = null)
    {
        return new ProviderActionInvocations(this._harness.Contexts, timeProvider ?? TimeProvider.System);
    }

    private ProviderInvocationReporter ReporterFor(
        Guid invocationId,
        ProviderActionInvocations invocations,
        ProviderAddInBinding binding)
    {
        return new ProviderInvocationReporter(
            invocationId,
            invocations,
            async ct =>
            {
                var stored = await this._harness.Credentials(binding).ReadAsync(ct);
                return [.. stored.Fields.Values];
            });
    }
}
