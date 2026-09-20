// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.ExampleAddIn;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using FactAttribute = Xunit.SkippableFactAttribute;
using TheoryAttribute = Xunit.SkippableTheoryAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers.Hosting;

/// <summary>
///     What a provider family reports about a connection's credential, and what the connection reflects once the
///     host has weighed that report against what it verified itself.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class ProviderCredentialHealthTests(PostgresContainerFixture fixture) : IAsyncLifetime
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

    [Theory]
    [InlineData(AiCredentialHealth.Healthy)]
    [InlineData(AiCredentialHealth.NeedsReauthorization)]
    [InlineData(AiCredentialHealth.Disabled)]
    public async Task AFamilyCanReportEachMemberOfTheSetAndTheConnectionReflectsIt(AiCredentialHealth health)
    {
        var binding = await this._harness.SeedConnectionAsync($"Reports {health}");

        await this.SignalFor(binding).ReportAsync(health, "what the family saw");

        var resolved = await this.HealthStore().GetAsync(binding.ConnectionProfileId);

        Assert.Equal(health, resolved.Health);
        Assert.Equal("what the family saw", resolved.Cause);
    }

    // The value set is the host's and is closed. A value outside it would put a state in the column that no host
    // logic can key on and no console can render.
    [Fact]
    public async Task AFamilyCannotReportAStateOutsideTheHostsSet()
    {
        var binding = await this._harness.SeedConnectionAsync("Invented state");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => this.SignalFor(binding).ReportAsync((AiCredentialHealth)99));
    }

    // A verification is a live check the host ran, so it replaces what a family said before it. That is what
    // makes re-verifying a connection the way an operator clears a reported problem.
    [Fact]
    public async Task AFreshVerificationReplacesAnEarlierReportFromTheFamily()
    {
        var binding = await this._harness.SeedConnectionAsync("Verified after reporting");

        await this.SignalFor(binding).ReportAsync(AiCredentialHealth.NeedsReauthorization, "the grant was revoked");
        await this.SeedVerificationAsync(binding, AiVerificationStatus.Verified, DateTimeOffset.UtcNow.AddMinutes(5));

        var resolved = await this.HealthStore().GetAsync(binding.ConnectionProfileId);

        Assert.Equal(AiCredentialHealth.Healthy, resolved.Health);
    }

    // A family that sees a revoked grant after a verification succeeded is reporting something the verification
    // could not have seen, so the report holds.
    [Fact]
    public async Task AReportMadeAfterAVerificationIsWhatTheConnectionReflects()
    {
        var binding = await this._harness.SeedConnectionAsync("Reported after verifying");

        await this.SeedVerificationAsync(binding, AiVerificationStatus.Verified, DateTimeOffset.UtcNow.AddMinutes(-5));
        await this.SignalFor(binding).ReportAsync(AiCredentialHealth.Disabled, "the provider disabled the account");

        var resolved = await this.HealthStore().GetAsync(binding.ConnectionProfileId);

        Assert.Equal(AiCredentialHealth.Disabled, resolved.Health);
    }

    // Enum.TryParse takes a numeric string and hands back whatever member carries that number, so a column
    // holding '99' would otherwise read as a health state nothing can key on and a console cannot render. It
    // reads as needing re-authorization, like any other name this build does not know.
    [Theory]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData("SomethingElse")]
    public void AStoredHealthThisBuildCannotNameReadsAsNeedingReauthorization(string stored)
    {
        var state = ProviderCredentialHealthResolver.FromReport(stored, "whatever it said", DateTimeOffset.UtcNow);

        Assert.NotNull(state);
        Assert.Equal(AiCredentialHealth.NeedsReauthorization, state.Health);
        Assert.Contains(stored, state.Cause, StringComparison.Ordinal);
    }

    // The classification is inferred from a failed call and the report is not, so the report holds regardless of
    // which arrived last.
    [Fact]
    public void AFamilysReportOutranksWhatTheRetryStageConcludedFromAFailedCall()
    {
        var resolved = ProviderCredentialHealthResolver.Resolve(
            verification: null,
            new ProviderCredentialHealthState(
                AiCredentialHealth.NeedsReauthorization,
                "the grant was revoked",
                DateTimeOffset.UtcNow.AddMinutes(-5)),
            new ProviderCredentialHealthState(
                AiCredentialHealth.Healthy,
                "the last call succeeded",
                DateTimeOffset.UtcNow));

        Assert.Equal(AiCredentialHealth.NeedsReauthorization, resolved.Health);
    }

    // A credential with no stated expiry reads as never usable wherever it is read, so every call would try to
    // renew it and a whole review would serialise through one row lock. Reported as needing re-authorization
    // instead, which names a cause an operator can act on.
    [Fact]
    public async Task AStoredCredentialWithNoExpiryIsReportedAsNeedingReauthorization()
    {
        var binding = await this._harness.SeedConnectionAsync("No expiry stored");
        await this._harness.SeedCredentialAsync(
            binding,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["accessToken"] = "a-token" },
            expiresAt: null);

        await this.SignalFor(binding).ReportAsync(AiCredentialHealth.Healthy);

        var resolved = await this.HealthStore().GetAsync(binding.ConnectionProfileId);

        Assert.Equal(AiCredentialHealth.NeedsReauthorization, resolved.Health);
        Assert.Contains("expires", resolved.Cause!, StringComparison.Ordinal);
    }

    // The cause is a string a family produced, so the host caps it and scrubs the connection's credential out of
    // it before it reaches a column. Several providers echo part of a presented key back in a refusal.
    [Fact]
    public async Task ACauseIsScrubbedOfTheConnectionsCredentialAndCappedBeforeItIsStored()
    {
        var binding = await this._harness.SeedConnectionAsync("Echoed credential");
        await this._harness.SeedCredentialAsync(
            binding,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["accessToken"] = "sk-the-secret-token" },
            DateTimeOffset.UtcNow.AddHours(1));

        await this.SignalFor(binding).ReportAsync(
            AiCredentialHealth.NeedsReauthorization,
            "The provider refused the key sk-the-secret-token. " + new string('x', 4000));

        await using var db = this._harness.CreateContext();
        var cause = await db.AiConnectionProfiles
            .AsNoTracking()
            .Where(profile => profile.Id == binding.ConnectionProfileId)
            .Select(profile => profile.CredentialHealthCause)
            .SingleAsync();

        Assert.NotNull(cause);
        Assert.DoesNotContain("sk-the-secret-token", cause, StringComparison.Ordinal);
        Assert.True(cause.Length <= ProviderHostLimits.MaximumMessageLength);
    }

    // A family whose credential is a key an operator typed has nothing to report about it, so its connections
    // carry no health state at all.
    [Fact]
    public async Task AFamilyThatDeclaresNoCredentialHealthHasNone()
    {
        var binding = await this._harness.SeedConnectionAsync("No health declared");
        await this.SignalFor(binding).ReportAsync(AiCredentialHealth.Disabled);

        var driver = Substitute.For<IAiProviderDriver>();
        driver.Declaration.Returns(new ExampleProviderDriver().Declaration with { HasCredentialHealth = false });

        var resolved = await this.HealthStore(driver).GetAsync(binding.ConnectionProfileId);

        Assert.Equal(AiCredentialHealth.Unreported, resolved.Health);
    }

    // A family reports from whichever replica served its call, and the reports arrive in whatever order those
    // calls finished. Written unconditionally, a slow report of a state the connection has left replaces the
    // current one, and the column is what the console shows and what a verification is weighed against.
    [Fact]
    public async Task AReportThatArrivesLateWithAnEarlierObservationDoesNotReplaceTheCurrentState()
    {
        var binding = await this._harness.SeedConnectionAsync("Out-of-order reports");
        var earlier = new FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(-5));
        var later = new FakeTimeProvider(DateTimeOffset.UtcNow);

        await this.SignalFor(binding, later).ReportAsync(AiCredentialHealth.Disabled, "the provider disabled it");
        await this.SignalFor(binding, earlier).ReportAsync(AiCredentialHealth.Healthy, "the call succeeded");

        var resolved = await this.HealthStore().GetAsync(binding.ConnectionProfileId);

        Assert.Equal(AiCredentialHealth.Disabled, resolved.Health);
        Assert.Equal("the provider disabled it", resolved.Cause);
    }

    // A statement that recorded when it was observed is the more recent of the two when the other recorded
    // nothing. Compared as two nullable times, every comparison involving the missing one is false, which
    // returns the verification whichever side the missing time is on.
    [Fact]
    public void AReportWithAnObservationTimeOutranksAVerificationThatRecordedNone()
    {
        var resolved = ProviderCredentialHealthResolver.Resolve(
            new ProviderCredentialHealthState(AiCredentialHealth.Healthy, "verified"),
            new ProviderCredentialHealthState(
                AiCredentialHealth.NeedsReauthorization,
                "the grant was revoked",
                DateTimeOffset.UtcNow),
            runtimeClassification: null);

        Assert.Equal(AiCredentialHealth.NeedsReauthorization, resolved.Health);
    }

    [Fact]
    public void AVerificationHoldsWhenNeitherSideRecordedWhenItWasObserved()
    {
        var resolved = ProviderCredentialHealthResolver.Resolve(
            new ProviderCredentialHealthState(AiCredentialHealth.Healthy, "verified"),
            new ProviderCredentialHealthState(AiCredentialHealth.Disabled, "reported"),
            runtimeClassification: null);

        Assert.Equal(AiCredentialHealth.Healthy, resolved.Health);
    }

    private ProviderHealthSignal SignalFor(ProviderAddInBinding binding, TimeProvider? timeProvider = null)
    {
        return new ProviderHealthSignal(
            binding,
            this._harness.Contexts,
            async ct =>
            {
                var stored = await this._harness.Credentials(binding).ReadAsync(ct);
                return [.. stored.Fields.Values];
            },
            timeProvider ?? TimeProvider.System);
    }

    private ProviderCredentialHealthStore HealthStore(IAiProviderDriver? driver = null)
    {
        var drivers = Substitute.For<IAiProviderDriverRegistry>();
        drivers.IsRegistered("example/provider").Returns(true);
        drivers.GetRequired("example/provider").Returns(driver ?? new ExampleProviderDriver());

        return new ProviderCredentialHealthStore(this._harness.Contexts, drivers, this._harness.Codec);
    }

    private async Task SeedVerificationAsync(
        ProviderAddInBinding binding,
        AiVerificationStatus status,
        DateTimeOffset checkedAt)
    {
        await using var db = this._harness.CreateContext();
        db.AiVerificationSnapshots.Add(
            new AiVerificationSnapshotRecord
            {
                ConnectionProfileId = binding.ConnectionProfileId,
                Status = status.ToString(),
                Summary = "what the host found",
                CheckedAt = checkedAt,
            });
        await db.SaveChangesAsync();
    }
}
