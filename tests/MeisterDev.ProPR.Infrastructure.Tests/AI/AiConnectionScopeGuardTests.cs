// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Infrastructure.Tests.Repositories;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

/// <summary>
///     Tests the rules a reference to a connection profile has to satisfy: it may only cross into the tenant that
///     owns the profile, and it may only use a provider family that tenant permits. Both live here because both
///     are asked at the same two moments — when a reference is written and before a credential is used.
/// </summary>
public sealed class AiConnectionScopeGuardTests
{
    private static readonly Guid TenantA = Guid.Parse("11111111-aaaa-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("22222222-bbbb-0000-0000-000000000002");
    private static readonly Guid ClientInA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid ClientInB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    private readonly IClientRegistry _clients = Substitute.For<IClientRegistry>();

    public AiConnectionScopeGuardTests()
    {
        this._clients.GetTenantIdAsync(ClientInA, Arg.Any<CancellationToken>()).Returns(TenantA);
        this._clients.GetTenantIdAsync(ClientInB, Arg.Any<CancellationToken>()).Returns(TenantB);
    }

    private readonly ITenantProviderPolicyProvider _policies = Substitute.For<ITenantProviderPolicyProvider>();

    // The connections below are on the Azure family, whose endpoint carries the operator's own resource name, so
    // what it declares are suffixes. That is the shape the endpoint restriction has to be checked by containment.
    private readonly IAiProviderDriverRegistry _drivers = DeclaringProviderFamilies.Declaring(
        "meisterdev/azureOpenAi",
        DeclaringProviderFamilies.DeclarationWith("meisterdev/azureOpenAi") with
        {
            ReachedHostPatterns = [".openai.azure.com"],
        });

    private AiConnectionScopeGuard Sut()
    {
        this._policies.GetForTenantAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(TenantProviderPolicy.Unrestricted);
        return new AiConnectionScopeGuard(this._clients, this._policies, this._drivers);
    }

    [Fact]
    public async Task TenantScopedConnection_ReferencedByOwningTenant_IsPermitted()
    {
        var connection = Connection(tenantId: TenantA);

        Assert.Null(await this.Sut().ValidateAsync(connection, TenantA));
    }

    [Fact]
    public async Task TenantScopedConnection_ReferencedByAnotherTenant_IsRefused()
    {
        var connection = Connection(tenantId: TenantA);

        var reason = await this.Sut().ValidateAsync(connection, TenantB);

        Assert.NotNull(reason);
        Assert.Contains("tenant", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClientScopedConnection_ReferencedFromSameTenant_IsPermitted()
    {
        // A connection owned by one client is still within its tenant, so a tenant-wide entry or a
        // sibling client in that same tenant may reference it. Only crossing a tenant boundary is refused.
        var connection = Connection(clientId: ClientInA);

        Assert.Null(await this.Sut().ValidateAsync(connection, TenantA));
    }

    [Fact]
    public async Task ClientScopedConnection_ReferencedFromAnotherTenant_IsRefused()
    {
        var connection = Connection(clientId: ClientInA);

        var reason = await this.Sut().ValidateAsync(connection, TenantB);

        Assert.NotNull(reason);
    }

    [Fact]
    public async Task ConnectionWithNoOwner_IsRefused()
    {
        // Neither tenant- nor client-scoped: the owning tenant cannot be established, so the reference
        // is refused rather than assumed safe.
        var connection = Connection();

        Assert.NotNull(await this.Sut().ValidateAsync(connection, TenantA));
    }

    [Fact]
    public async Task ClientScopedConnection_WhoseClientHasNoTenant_IsRefused()
    {
        var orphan = Guid.Parse("cccccccc-0000-0000-0000-00000000000c");
        this._clients.GetTenantIdAsync(orphan, Arg.Any<CancellationToken>()).Returns((Guid?)null);
        var connection = Connection(clientId: orphan);

        Assert.NotNull(await this.Sut().ValidateAsync(connection, TenantA));
    }

    // The runtime half of the allow-list: a profile inside the right tenant is still refused when its provider
    // family is not on that tenant's list, and the refusal explains itself rather than reading as a scope error.
    [Fact]
    public async Task ConnectionWhoseProviderTheTenantForbids_IsRefused()
    {
        var connection = Connection(tenantId: TenantA);
        this._policies.GetForTenantAsync(TenantA, Arg.Any<CancellationToken>())
            .Returns(new TenantProviderPolicy(["meisterdev/openAiCompatible"]));
        var guard = new AiConnectionScopeGuard(this._clients, this._policies, this._drivers);

        var reason = await guard.ValidateAsync(connection, TenantA);

        Assert.NotNull(reason);
        Assert.Contains("meisterdev/azureOpenAi", reason, StringComparison.Ordinal);
        Assert.Contains("permitted provider list", reason, StringComparison.Ordinal);
    }

    // An allow-list whose entries this build cannot name permits nothing, so a profile inside the right tenant is
    // still refused. The reason names the entry that was not understood, so the refusal is actionable.
    [Fact]
    public async Task ConnectionUnderAnAllowListNamingNoKnownFamily_IsRefusedAndTheEntryIsNamed()
    {
        var connection = Connection(tenantId: TenantA);

        // The policy is read before the substitute is configured: reading the registry inside Returns() would
        // attach the return value to that read rather than to the policy read.
        var unreadable = TenantProviderPolicy.FromStored(["Acme.Llm"], [], this._drivers);
        this._policies.GetForTenantAsync(TenantA, Arg.Any<CancellationToken>()).Returns(unreadable);
        var guard = new AiConnectionScopeGuard(this._clients, this._policies, this._drivers);

        var reason = await guard.ValidateAsync(connection, TenantA);

        Assert.NotNull(reason);
        Assert.Contains("permitted provider list", reason, StringComparison.Ordinal);
        Assert.Contains("Acme.Llm", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectionWhoseProviderTheTenantPermits_IsAllowed()
    {
        var connection = Connection(tenantId: TenantA);
        this._policies.GetForTenantAsync(TenantA, Arg.Any<CancellationToken>())
            .Returns(new TenantProviderPolicy(["meisterdev/azureOpenAi"]));
        var guard = new AiConnectionScopeGuard(this._clients, this._policies, this._drivers);

        Assert.Null(await guard.ValidateAsync(connection, TenantA));
    }

    // The endpoint leg of the same policy. A profile inside the right tenant, on a permitted family, is still
    // refused when its host is not on the tenant's endpoint list.
    [Fact]
    public async Task ConnectionWhoseEndpointHostTheTenantForbids_IsRefused()
    {
        var connection = Connection(tenantId: TenantA);
        this._policies.GetForTenantAsync(TenantA, Arg.Any<CancellationToken>())
            .Returns(new TenantProviderPolicy([], ["opencode.ai"]));
        var guard = new AiConnectionScopeGuard(this._clients, this._policies, this._drivers);

        var reason = await guard.ValidateAsync(connection, TenantA);

        Assert.NotNull(reason);
        Assert.Contains("test.openai.azure.com", reason, StringComparison.Ordinal);
        Assert.Contains("permitted endpoint list", reason, StringComparison.Ordinal);
    }

    // Every member of the set has to be permitted. The base URL of this profile is on the tenant's list, and the
    // family also reaches every other resource under the same suffix, which the tenant has not permitted.
    [Fact]
    public async Task ConnectionWhoseFamilyReachesMoreThanTheTenantPermits_IsRefusedAndThePatternIsNamed()
    {
        var connection = Connection(tenantId: TenantA);
        this._policies.GetForTenantAsync(TenantA, Arg.Any<CancellationToken>())
            .Returns(new TenantProviderPolicy([], ["test.openai.azure.com"]));
        var guard = new AiConnectionScopeGuard(this._clients, this._policies, this._drivers);

        var reason = await guard.ValidateAsync(connection, TenantA);

        Assert.NotNull(reason);
        Assert.Contains(".openai.azure.com", reason, StringComparison.Ordinal);
        Assert.Contains("permitted endpoint list", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectionWhoseBaseUrlAndDeclaredPatternsTheTenantPermits_IsAllowed()
    {
        var connection = Connection(tenantId: TenantA);
        this._policies.GetForTenantAsync(TenantA, Arg.Any<CancellationToken>())
            .Returns(new TenantProviderPolicy([], [".openai.azure.com"]));
        var guard = new AiConnectionScopeGuard(this._clients, this._policies, this._drivers);

        Assert.Null(await guard.ValidateAsync(connection, TenantA));
    }

    // The case the declaration exists for: a family whose endpoint is fixed by its vendor carries no base URL,
    // so without the declared patterns the tenant's endpoint restriction would have nothing to read.
    [Fact]
    public async Task ConnectionWithNoBaseUrl_IsStillSubjectToTheEndpointRestriction()
    {
        var connection = Connection(tenantId: TenantA, baseUrl: string.Empty);
        this._policies.GetForTenantAsync(TenantA, Arg.Any<CancellationToken>())
            .Returns(new TenantProviderPolicy([], ["opencode.ai"]));
        var guard = new AiConnectionScopeGuard(this._clients, this._policies, this._drivers);

        var reason = await guard.ValidateAsync(connection, TenantA);

        Assert.NotNull(reason);
        Assert.Contains(".openai.azure.com", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectionWithNoBaseUrlOnAFamilyTheTenantPermitsTheReachOf_IsAllowed()
    {
        var connection = Connection(tenantId: TenantA, baseUrl: string.Empty);
        this._policies.GetForTenantAsync(TenantA, Arg.Any<CancellationToken>())
            .Returns(new TenantProviderPolicy([], [".azure.com"]));
        var guard = new AiConnectionScopeGuard(this._clients, this._policies, this._drivers);

        Assert.Null(await guard.ValidateAsync(connection, TenantA));
    }

    // The two rules are independent: a tenant that has stated no policy still cannot reference a profile owned by
    // another tenant, and the allow-list has nothing to say about one it owns.
    [Fact]
    public async Task WithAnUnrestrictedPolicy_TheTenantBoundaryIsStillEnforced()
    {
        var guard = this.Sut();

        Assert.Null(await guard.ValidateAsync(Connection(tenantId: TenantA), TenantA));
        Assert.NotNull(await guard.ValidateAsync(Connection(tenantId: TenantA), TenantB));
    }

    private static AiConnectionDto Connection(
        Guid? clientId = null,
        Guid? tenantId = null,
        string baseUrl = "https://test.openai.azure.com")
    {
        var now = DateTimeOffset.UtcNow;
        return new AiConnectionDto(
            Guid.NewGuid(),
            clientId,
            "Scoped Connection",
            "meisterdev/azureOpenAi",
            baseUrl,
            "meisterdev/azureOpenAi:ApiKey",
            AiDiscoveryMode.ManualOnly,
            true,
            [],
            [],
            AiVerificationResultDto.NeverVerified,
            now,
            now,
            Secret: "secret",
            TenantId: tenantId);
    }
}
