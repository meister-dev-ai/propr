// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Hosting;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers.Hosting;

/// <summary>
///     What the host checks, and what it records as the owner, when an action stores the credential it produced.
/// </summary>
/// <remarks>
///     An action outlives the call that started it. A role can be revoked and an entitlement can lapse while the
///     administrator is at the provider, so the checks made when the action started are not checks at the moment
///     the credential is written.
/// </remarks>
[Collection("PostgresIntegration")]
public sealed class ProviderActionCredentialGrantTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private const string CapabilityKey = "example-connections";

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

    [Fact]
    public async Task ACredentialIsStoredWhileTheOwnerRoleAndTheCapabilityBothStillHold()
    {
        var tenant = await this._harness.SeedTenantAsync();
        var initiator = await this._harness.SeedAdministratorAsync(tenantId: tenant);
        var binding = await this._harness.SeedConnectionAsync(
            "Stores the credential",
            requiredCapabilityKey: CapabilityKey,
            tenantId: tenant);

        var outcome = await this.RunStoringActionAsync(binding, tenant, initiator);

        Assert.Equal(ProviderInvocationState.Completed, outcome.Invocation!.State);

        await using var db = this._harness.CreateContext();
        var profile = await db.AiConnectionProfiles.SingleAsync(each => each.Id == binding.ConnectionProfileId);

        Assert.NotNull(profile.ProtectedSecret);
        Assert.Equal(initiator, profile.CredentialOwnerAdminId);
        Assert.NotNull(profile.CredentialOwnerDisplayName);
        Assert.NotNull(profile.CredentialAuthorizedAt);
    }

    // A grant is issued against one administrator's account at the provider and revoking it means revoking that
    // account's grant, so the timestamp beside it is read at the same instant everything else on the row is.
    [Fact]
    public async Task TheAuthorizationTimestampIsWrittenInCoordinatedUniversalTime()
    {
        var tenant = await this._harness.SeedTenantAsync();
        var initiator = await this._harness.SeedAdministratorAsync(tenantId: tenant);
        var binding = await this._harness.SeedConnectionAsync(
            "Timestamps the grant",
            requiredCapabilityKey: CapabilityKey,
            tenantId: tenant);

        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        await this.RunStoringActionAsync(binding, tenant, initiator);

        await using var db = this._harness.CreateContext();
        var authorizedAt = await db.AiConnectionProfiles
            .Where(each => each.Id == binding.ConnectionProfileId)
            .Select(each => each.CredentialAuthorizedAt)
            .SingleAsync();

        Assert.NotNull(authorizedAt);
        Assert.Equal(TimeSpan.Zero, authorizedAt.Value.Offset);
        Assert.InRange(authorizedAt.Value, before, DateTimeOffset.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task ARoleRevokedBetweenTheStartAndTheCompletionStopsTheStoreAndTheRunSaysWhy()
    {
        var tenant = await this._harness.SeedTenantAsync();
        var initiator = await this._harness.SeedAdministratorAsync(tenantId: tenant);
        var binding = await this._harness.SeedConnectionAsync(
            "Role revoked",
            requiredCapabilityKey: CapabilityKey,
            tenantId: tenant);

        var outcome = await this.RunStoringActionAsync(
            binding,
            tenant,
            initiator,
            beforeTheStore: () => this._harness.RevokeTenantMembershipsAsync(initiator));

        Assert.Equal(ProviderInvocationState.Failed, outcome.Invocation!.State);
        Assert.Contains(
            "tenant administrator",
            outcome.Invocation.TerminalMessage!,
            StringComparison.OrdinalIgnoreCase);

        await using var db = this._harness.CreateContext();
        var profile = await db.AiConnectionProfiles.SingleAsync(each => each.Id == binding.ConnectionProfileId);

        Assert.Null(profile.ProtectedSecret);
        Assert.Null(profile.CredentialOwnerAdminId);
    }

    // The licence check the review path makes fires on use. This one fires on the write, and that keeps an
    // installation whose entitlement lapsed from acquiring a new credential it is no longer entitled to use.
    [Fact]
    public async Task AnEntitlementThatLapsesBetweenTheStartAndTheCompletionStopsTheStoreAndNamesTheRequirement()
    {
        var tenant = await this._harness.SeedTenantAsync();
        var initiator = await this._harness.SeedAdministratorAsync(tenantId: tenant);
        var binding = await this._harness.SeedConnectionAsync(
            "Entitlement lapsed",
            requiredCapabilityKey: CapabilityKey,
            tenantId: tenant);

        var outcome = await this.RunStoringActionAsync(
            binding,
            tenant,
            initiator,
            beforeTheStore: () =>
            {
                this._harness.Capabilities
                    .IsAvailableAsync(CapabilityKey, Arg.Any<CancellationToken>())
                    .Returns(false);
                this._harness.Capabilities
                    .IsAvailableNowAsync(CapabilityKey, Arg.Any<CancellationToken>())
                    .Returns(false);
                return Task.CompletedTask;
            });

        Assert.Equal(ProviderInvocationState.Failed, outcome.Invocation!.State);
        Assert.Contains(CapabilityKey, outcome.Invocation.TerminalMessage!, StringComparison.Ordinal);

        await using var db = this._harness.CreateContext();
        var profile = await db.AiConnectionProfiles.SingleAsync(each => each.Id == binding.ConnectionProfileId);

        Assert.Null(profile.ProtectedSecret);
    }

    // The answer the review path holds is up to a minute old, which is longer than the interval this write has to
    // be correct over: the flow it belongs to takes minutes, and the entitlement can have lapsed before the
    // action even started. The write-time check resolves the answer instead of taking the held one.
    [Fact]
    public async Task TheStoreIsRefusedOnAResolvedEntitlementEvenWhileTheHeldAnswerStillSaysYes()
    {
        var tenant = await this._harness.SeedTenantAsync();
        var initiator = await this._harness.SeedAdministratorAsync(tenantId: tenant);
        var binding = await this._harness.SeedConnectionAsync(
            "Held answer is stale",
            requiredCapabilityKey: CapabilityKey,
            tenantId: tenant);

        // The held answer is left saying yes, which the review path would keep reading for the rest of
        // the minute it holds one for.
        this._harness.Capabilities
            .IsAvailableNowAsync(CapabilityKey, Arg.Any<CancellationToken>())
            .Returns(false);

        var outcome = await this.RunStoringActionAsync(binding, tenant, initiator);

        Assert.Equal(ProviderInvocationState.Failed, outcome.Invocation!.State);
        Assert.Contains(CapabilityKey, outcome.Invocation.TerminalMessage!, StringComparison.Ordinal);

        await using var db = this._harness.CreateContext();
        var stored = await db.AiConnectionProfiles
            .Where(each => each.Id == binding.ConnectionProfileId)
            .Select(each => each.ProtectedSecret)
            .SingleAsync();

        Assert.Null(stored);
    }

    // The paste path is where another administrator can complete a flow they did not start. Continuing a run
    // takes no administrator of its own, so there is nothing a submitter could be recorded as: the owner is read
    // off the run, and the run records who started it.
    [Fact]
    public async Task AnAdministratorWhoOnlyCompletesTheFlowDoesNotBecomeTheRecordedOwner()
    {
        var tenant = await this._harness.SeedTenantAsync();
        var initiator = await this._harness.SeedAdministratorAsync(tenantId: tenant);
        var submitter = await this._harness.SeedAdministratorAsync(tenantId: tenant);
        var binding = await this._harness.SeedConnectionAsync(
            "Completed by another administrator",
            requiredCapabilityKey: CapabilityKey,
            tenantId: tenant);

        var driver = ScriptedActionDriver.Doing(
            async (_, inputs, context) =>
            {
                if (inputs.Count == 0)
                {
                    return ProviderActionResult.ShowForm([]);
                }

                await StoreAsync(context);
                return ProviderActionResult.Completed("Connected.");
            },
            ScriptedActionDriver.Declaring(capabilityKey: CapabilityKey));

        var dispatcher = this.Dispatcher(driver);
        var connection = Connection(binding, tenant);

        var started = await dispatcher.DispatchAsync(
            connection,
            driver.Declaration.Key,
            ScriptedActionDriver.ActionId,
            initiator);

        var continued = await dispatcher.ContinueAsync(
            connection,
            started.Invocation!,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["callbackUrl"] = "https://auth.example.com/cb" });

        Assert.Equal(ProviderInvocationState.Completed, continued.Invocation!.State);
        Assert.Equal(initiator, continued.Invocation.InitiatingAdminId);

        await using var db = this._harness.CreateContext();
        var profile = await db.AiConnectionProfiles.SingleAsync(each => each.Id == binding.ConnectionProfileId);

        Assert.Equal(initiator, profile.CredentialOwnerAdminId);
        Assert.NotEqual(submitter, profile.CredentialOwnerAdminId);
    }

    private static async Task StoreAsync(IProviderActionContext context)
    {
        await using var session = await context.Credentials.OpenAsync(context.Cancellation);
        await session.WriteAsync(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["apiKey"] = "the-new-credential" },
            DateTimeOffset.UtcNow.AddHours(1),
            context.Cancellation);
        await session.CompleteAsync(context.Cancellation);
    }

    private static AiConnectionDto Connection(ProviderAddInBinding binding, Guid tenantId)
    {
        return new AiConnectionDto(
            binding.ConnectionProfileId,
            null,
            binding.ConnectionDisplayName,
            binding.AddInKey,
            "https://api.example.com/v1",
            binding.AddInKey + ":ApiKey",
            AiDiscoveryMode.ManualOnly,
            IsActive: true,
            [],
            [],
            AiVerificationResultDto.NeverVerified,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            TenantId: tenantId);
    }

    private async Task<ProviderActionDispatchOutcome> RunStoringActionAsync(
        ProviderAddInBinding binding,
        Guid tenantId,
        Guid initiator,
        Func<Task>? beforeTheStore = null)
    {
        var driver = ScriptedActionDriver.Doing(
            async (_, _, context) =>
            {
                // Stands in for the minutes the operator spends at the vendor, which is where a role is revoked
                // and an entitlement lapses.
                if (beforeTheStore is not null)
                {
                    await beforeTheStore();
                }

                await StoreAsync(context);
                return ProviderActionResult.Completed("Connected.");
            },
            ScriptedActionDriver.Declaring(capabilityKey: CapabilityKey));

        return await this.Dispatcher(driver).DispatchAsync(
            Connection(binding, tenantId),
            driver.Declaration.Key,
            ScriptedActionDriver.ActionId,
            initiator);
    }

    private ProviderActionDispatcher Dispatcher(ScriptedActionDriver driver)
    {
        var registry = new AiProviderRegistry([driver]);
        var invocations = new ProviderActionInvocations(this._harness.Contexts, TimeProvider.System);
        var signals = new ProviderInvocationCancellation();
        var ownerRoles = new ProviderConnectionOwnerRoles(this._harness.Contexts);

        var contexts = new ProviderConnectionContextFactory(
            Substitute.For<IHttpMessageHandlerFactory>(),
            this._harness.Contexts,
            this._harness.Codec,
            this._harness.Capabilities,
            ownerRoles,
            registry,
            invocations,
            signals,
            TimeProvider.System);

        return new ProviderActionDispatcher(
            registry,
            invocations,
            signals,
            contexts,
            this._harness.Capabilities,
            ownerRoles,
            TimeProvider.System,
            NullLogger<ProviderActionDispatcher>.Instance);
    }
}
