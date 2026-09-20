// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.Ai.Providers.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Controllers;

public sealed class LogicalModelsControllerTests
{
    // The family these cases stand a connection up against, and the one credential shape it declares.
    private const string ConnectableFamily = "tests/connectable";

    private const string ConnectableApiKey = ConnectableFamily + ":ApiKey";

    private static readonly Guid ClientId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid TenantId = Guid.Parse("dddddddd-0000-0000-0000-000000000002");

    private static LogicalModelWriteRequest Write(string name = "deep")
    {
        return new LogicalModelWriteRequest(name, AiOperationKind.Chat, Guid.NewGuid(), Guid.NewGuid());
    }

    // ---- ClientLogicalModelsController ----

    [Fact]
    public async Task CreateOverride_WithoutClientAdmin_IsForbidden()
    {
        var catalog = Substitute.For<ILogicalModelCatalogRepository>();
        var controller = ClientController(catalog, clientAdmin: false);

        var result = await controller.CreateOverride(ClientId, Write(), default);

        Assert.Equal(StatusCodes.Status403Forbidden, ((ObjectResult)result).StatusCode);
        await catalog.DidNotReceive().AddClientOverrideAsync(Arg.Any<Guid>(), Arg.Any<LogicalModelDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateOverride_AsClientAdmin_Returns201()
    {
        var catalog = Substitute.For<ILogicalModelCatalogRepository>();
        var controller = ClientController(catalog, clientAdmin: true);

        var result = await controller.CreateOverride(ClientId, Write(), default);

        Assert.IsType<CreatedAtActionResult>(result);
        await catalog.Received(1).AddClientOverrideAsync(ClientId, Arg.Is<LogicalModelDto>(d => d.Name == "deep"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateOverride_DuplicateName_Returns409()
    {
        var catalog = Substitute.For<ILogicalModelCatalogRepository>();
        catalog.AddClientOverrideAsync(ClientId, Arg.Any<LogicalModelDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new DuplicateLogicalModelException("deep")));
        var controller = ClientController(catalog, clientAdmin: true);

        var result = await controller.CreateOverride(ClientId, Write(), default);

        Assert.Equal(StatusCodes.Status409Conflict, ((ObjectResult)result).StatusCode);
    }

    [Fact]
    public async Task CreateOverride_InvalidModel_Returns400()
    {
        var catalog = Substitute.For<ILogicalModelCatalogRepository>();
        catalog.AddClientOverrideAsync(ClientId, Arg.Any<LogicalModelDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new LogicalModelReferenceInvalidException("deep", "model does not support chat")));
        var controller = ClientController(catalog, clientAdmin: true);

        var result = await controller.CreateOverride(ClientId, Write(), default);

        Assert.Equal(StatusCodes.Status400BadRequest, ((ObjectResult)result).StatusCode);
    }

    [Fact]
    public async Task CreateOverride_BlankName_Returns400Validation()
    {
        var catalog = Substitute.For<ILogicalModelCatalogRepository>();
        var controller = ClientController(catalog, clientAdmin: true);

        var result = await controller.CreateOverride(ClientId, Write(name: "  "), default);

        // A blank name is rejected as a validation problem before anything is persisted.
        Assert.IsNotType<CreatedAtActionResult>(result);
        var details = Assert.IsAssignableFrom<ValidationProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
        Assert.Contains("name", details.Errors.Keys, StringComparer.OrdinalIgnoreCase);
        await catalog.DidNotReceive().AddClientOverrideAsync(Arg.Any<Guid>(), Arg.Any<LogicalModelDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteOverride_NotFound_Returns404()
    {
        var catalog = Substitute.For<ILogicalModelCatalogRepository>();
        catalog.DeleteClientOverrideAsync(ClientId, "ghost", Arg.Any<CancellationToken>()).Returns(false);
        var controller = ClientController(catalog, clientAdmin: true);

        Assert.IsType<NotFoundResult>(await controller.DeleteOverride(ClientId, "ghost", default));
    }

    [Fact]
    public async Task UpdateOverride_AsClientAdmin_Returns204()
    {
        var catalog = Substitute.For<ILogicalModelCatalogRepository>();
        catalog.UpdateClientOverrideAsync(ClientId, "deep", Arg.Any<LogicalModelDto>(), Arg.Any<CancellationToken>()).Returns(true);
        var controller = ClientController(catalog, clientAdmin: true);

        Assert.IsType<NoContentResult>(await controller.UpdateOverride(ClientId, "deep", Write(), default));
        await catalog.Received(1).UpdateClientOverrideAsync(ClientId, "deep", Arg.Any<LogicalModelDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateOverride_NotFound_Returns404()
    {
        var catalog = Substitute.For<ILogicalModelCatalogRepository>();
        catalog.UpdateClientOverrideAsync(ClientId, "ghost", Arg.Any<LogicalModelDto>(), Arg.Any<CancellationToken>()).Returns(false);
        var controller = ClientController(catalog, clientAdmin: true);

        Assert.IsType<NotFoundResult>(await controller.UpdateOverride(ClientId, "ghost", Write(name: "ghost"), default));
    }

    [Fact]
    public async Task UpdateOverride_InvalidModel_Returns400()
    {
        var catalog = Substitute.For<ILogicalModelCatalogRepository>();
        catalog.UpdateClientOverrideAsync(ClientId, "deep", Arg.Any<LogicalModelDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new LogicalModelReferenceInvalidException("deep", "model does not support chat")));
        var controller = ClientController(catalog, clientAdmin: true);

        var result = await controller.UpdateOverride(ClientId, "deep", Write(), default);

        Assert.Equal(StatusCodes.Status400BadRequest, ((ObjectResult)result).StatusCode);
    }

    [Fact]
    public async Task UpdateOverride_NameMismatch_Returns400AndDoesNotUpdate()
    {
        var catalog = Substitute.For<ILogicalModelCatalogRepository>();
        var controller = ClientController(catalog, clientAdmin: true);

        // The body name must match the route key; a divergent name is rejected rather than silently ignored.
        var result = await controller.UpdateOverride(ClientId, "deep", Write(name: "other"), default);

        var details = Assert.IsAssignableFrom<ValidationProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
        Assert.Contains("name", details.Errors.Keys, StringComparer.OrdinalIgnoreCase);
        await catalog.DidNotReceive().UpdateClientOverrideAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<LogicalModelDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListEffective_OverridesShadowTenantEntriesByName()
    {
        var catalog = Substitute.For<ILogicalModelCatalogRepository>();
        catalog.GetClientOverridesAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(new[] { Dto("deep"), Dto("fast") });
        catalog.GetTenantEntriesForClientAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(new[] { Dto("deep"), Dto("wide") });
        var controller = ClientController(catalog, clientAdmin: true);

        var ok = Assert.IsType<OkObjectResult>(await controller.ListEffective(ClientId, default));
        var entries = Assert.IsAssignableFrom<IEnumerable<LogicalModelResponse>>(ok.Value).ToList();

        // deep + fast from the client (deep shadows the tenant deep), wide inherited from the tenant = 3 total.
        Assert.Equal(3, entries.Count);
        Assert.Equal("client", entries.Single(e => e.Name == "deep").Scope);
        Assert.Equal("client", entries.Single(e => e.Name == "fast").Scope);
        Assert.Equal("tenant", entries.Single(e => e.Name == "wide").Scope);
    }

    [Fact]
    public async Task SetPurposeRole_AsClientAdmin_Returns204()
    {
        var catalog = Substitute.For<ILogicalModelCatalogRepository>();
        var controller = ClientController(catalog, clientAdmin: true);

        var result = await controller.SetPurposeRole(ClientId, AiPurpose.ReviewTriage, new SetPurposeRoleRequest("triage-role"), default);

        Assert.IsType<NoContentResult>(result);
        await catalog.Received(1).SetPurposeRoleAsync(ClientId, AiPurpose.ReviewTriage, "triage-role", Arg.Any<CancellationToken>());
    }

    // ---- TenantLogicalModelsController ----

    [Fact]
    public async Task TenantCreate_WithoutTenantAdmin_IsForbidden()
    {
        var catalog = Substitute.For<ILogicalModelCatalogRepository>();
        var controller = TenantController(catalog, tenantAdmin: false);

        var result = await controller.Create(TenantId, Write(), default);

        Assert.Equal(StatusCodes.Status403Forbidden, ((ObjectResult)result).StatusCode);
        await catalog.DidNotReceive().AddTenantEntryAsync(Arg.Any<Guid>(), Arg.Any<LogicalModelDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TenantCreate_AsTenantAdmin_Returns201()
    {
        var catalog = Substitute.For<ILogicalModelCatalogRepository>();
        var controller = TenantController(catalog, tenantAdmin: true);

        Assert.IsType<CreatedAtActionResult>(await controller.Create(TenantId, Write(), default));
        await catalog.Received(1).AddTenantEntryAsync(TenantId, Arg.Is<LogicalModelDto>(d => d.Name == "deep"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TenantCreate_SystemTenant_Returns400()
    {
        var catalog = Substitute.For<ILogicalModelCatalogRepository>();
        catalog.AddTenantEntryAsync(TenantId, Arg.Any<LogicalModelDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new SystemTenantLogicalModelCatalogException()));
        var controller = TenantController(catalog, tenantAdmin: true);

        var result = await controller.Create(TenantId, Write(), default);

        Assert.Equal(StatusCodes.Status400BadRequest, ((ObjectResult)result).StatusCode);
    }

    [Fact]
    public async Task TenantUpdate_AsTenantAdmin_Returns204()
    {
        var catalog = Substitute.For<ILogicalModelCatalogRepository>();
        catalog.UpdateTenantEntryAsync(TenantId, "deep", Arg.Any<LogicalModelDto>(), Arg.Any<CancellationToken>()).Returns(true);
        var controller = TenantController(catalog, tenantAdmin: true);

        Assert.IsType<NoContentResult>(await controller.Update(TenantId, "deep", Write(), default));
        await catalog.Received(1).UpdateTenantEntryAsync(TenantId, "deep", Arg.Any<LogicalModelDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TenantUpdate_NotFound_Returns404()
    {
        var catalog = Substitute.For<ILogicalModelCatalogRepository>();
        catalog.UpdateTenantEntryAsync(TenantId, "ghost", Arg.Any<LogicalModelDto>(), Arg.Any<CancellationToken>()).Returns(false);
        var controller = TenantController(catalog, tenantAdmin: true);

        Assert.IsType<NotFoundResult>(await controller.Update(TenantId, "ghost", Write(name: "ghost"), default));
    }

    [Fact]
    public async Task TenantUpdate_NameMismatch_Returns400AndDoesNotUpdate()
    {
        var catalog = Substitute.For<ILogicalModelCatalogRepository>();
        var controller = TenantController(catalog, tenantAdmin: true);

        var result = await controller.Update(TenantId, "deep", Write(name: "other"), default);

        var details = Assert.IsAssignableFrom<ValidationProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
        Assert.Contains("name", details.Errors.Keys, StringComparer.OrdinalIgnoreCase);
        await catalog.DidNotReceive().UpdateTenantEntryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<LogicalModelDto>(), Arg.Any<CancellationToken>());
    }

    // ---- TenantAiConnectionsController ----

    [Fact]
    public async Task TenantConnections_WithoutTenantAdmin_IsForbidden()
    {
        var repo = Substitute.For<IAiConnectionRepository>();
        var controller = TenantConnectionsController(repo, tenantAdmin: false);

        var result = await controller.List(TenantId, default);

        Assert.Equal(StatusCodes.Status403Forbidden, ((ObjectResult)result).StatusCode);
        await repo.DidNotReceive().GetByTenantAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TenantConnections_AsTenantAdmin_ReturnsTenantConnections()
    {
        var repo = Substitute.For<IAiConnectionRepository>();
        repo.GetByTenantAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(new[] { Connection("Azure") });
        var controller = TenantConnectionsController(repo, tenantAdmin: true);

        var ok = Assert.IsType<OkObjectResult>(await controller.List(TenantId, default));
        var listed = Assert.IsAssignableFrom<IEnumerable<AiConnectionDto>>(ok.Value).ToList();

        Assert.Single(listed);
        Assert.Equal("Azure", listed[0].DisplayName);
    }

    [Fact]
    public async Task TenantConnections_Delete_ForAnotherTenantsConnection_Returns404()
    {
        var repo = Substitute.For<IAiConnectionRepository>();
        var connectionId = Guid.NewGuid();
        // Owned by a different tenant, so deleting it via this tenant's route is a 404.
        repo.GetByIdAsync(connectionId, Arg.Any<CancellationToken>()).Returns(Connection("Azure", tenantId: Guid.NewGuid()));
        var controller = TenantConnectionsController(repo, tenantAdmin: true);

        Assert.IsType<NotFoundResult>(await controller.Delete(TenantId, connectionId, default));
        await repo.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // A stored target is checked again before it is contacted. A row saved before a check existed, or while
    // private egress was permitted, can hold a URL the driver now refuses, and verifying it would send the
    // stored credential there.
    [Fact]
    public async Task TenantConnections_Verify_WhenTheDriverRefusesTheStoredTarget_DoesNotContactIt()
    {
        var connectionId = Guid.NewGuid();
        var repo = Substitute.For<IAiConnectionRepository>();
        repo.GetByIdAsync(connectionId, Arg.Any<CancellationToken>())
            .Returns(Connection("OpenAI", secret: "a-key"));
        var driver = ConnectableDriver();
        driver.ValidateProbeTarget(Arg.Any<AiProbeTarget>())
            .Returns("This endpoint is no longer reachable under the egress policy.");
        var controller = TenantConnectionsController(repo, tenantAdmin: true, driver);

        var result = await controller.Verify(TenantId, connectionId, default);

        var details = Assert.IsAssignableFrom<ValidationProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
        Assert.Contains("baseUrl", details.Errors.Keys, StringComparer.OrdinalIgnoreCase);
        await driver.DidNotReceive().VerifyAsync(Arg.Any<ProviderEndpoint>(), Arg.Any<CancellationToken>());
        await repo.DidNotReceive().SaveVerificationAsync(Arg.Any<Guid>(), Arg.Any<AiVerificationResultDto>(), Arg.Any<CancellationToken>());
    }

    // A stored credential that is not the one the family reads fails at the provider with a message naming
    // neither the field nor the family, so it is refused here and the field is named.
    [Fact]
    public async Task TenantConnections_Verify_WithAStoredCredentialTheFamilyCannotRead_IsRefusedNamingTheField()
    {
        var connectionId = Guid.NewGuid();
        var repo = Substitute.For<IAiConnectionRepository>();
        repo.GetByIdAsync(connectionId, Arg.Any<CancellationToken>()).Returns(Connection("OpenAI"));
        var driver = ConnectableDriver();
        var controller = TenantConnectionsController(repo, tenantAdmin: true, driver);

        var result = await controller.Verify(TenantId, connectionId, default);

        var details = Assert.IsAssignableFrom<ValidationProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
        Assert.Contains(
            details.Errors.Values.SelectMany(reasons => reasons),
            reason => reason.Contains(ProviderSecretEnvelope.ApiKeyField, StringComparison.Ordinal));
        await driver.DidNotReceive().VerifyAsync(Arg.Any<ProviderEndpoint>(), Arg.Any<CancellationToken>());
    }

    // A tenant that tightens its endpoint list after a profile was saved has to be able to rely on it: verifying
    // sends the stored credential, so the host is asked about before the profile is dialled rather than only
    // where it was written.
    [Fact]
    public async Task TenantConnections_Verify_WhenTheTenantNoLongerPermitsTheHost_DoesNotContactIt()
    {
        var connectionId = Guid.NewGuid();
        var repo = Substitute.For<IAiConnectionRepository>();
        repo.GetByIdAsync(connectionId, Arg.Any<CancellationToken>())
            .Returns(Connection("OpenAI", secret: "a-key"));
        var driver = ConnectableDriver();
        var controller = TenantConnectionsController(
            repo,
            tenantAdmin: true,
            driver,
            new TenantProviderPolicy([], ["api.openai.com"]));

        var result = await controller.Verify(TenantId, connectionId, default);

        var details = Assert.IsAssignableFrom<ValidationProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
        Assert.Contains("baseUrl", details.Errors.Keys, StringComparer.OrdinalIgnoreCase);
        await driver.DidNotReceive().VerifyAsync(Arg.Any<ProviderEndpoint>(), Arg.Any<CancellationToken>());
        await repo.DidNotReceive().SaveVerificationAsync(Arg.Any<Guid>(), Arg.Any<AiVerificationResultDto>(), Arg.Any<CancellationToken>());
    }

    // The family leg of the same policy, for the same reason.
    [Fact]
    public async Task TenantConnections_Verify_WhenTheTenantNoLongerPermitsTheFamily_DoesNotContactIt()
    {
        var connectionId = Guid.NewGuid();
        var repo = Substitute.For<IAiConnectionRepository>();
        repo.GetByIdAsync(connectionId, Arg.Any<CancellationToken>())
            .Returns(Connection("OpenAI", secret: "a-key"));
        var driver = ConnectableDriver();
        var controller = TenantConnectionsController(
            repo,
            tenantAdmin: true,
            driver,
            new TenantProviderPolicy(["meisterdev/azureOpenAi"]));

        var result = await controller.Verify(TenantId, connectionId, default);

        var details = Assert.IsAssignableFrom<ValidationProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
        Assert.Contains("providerKind", details.Errors.Keys, StringComparer.OrdinalIgnoreCase);
        await driver.DidNotReceive().VerifyAsync(Arg.Any<ProviderEndpoint>(), Arg.Any<CancellationToken>());
        await repo.DidNotReceive().SaveVerificationAsync(Arg.Any<Guid>(), Arg.Any<AiVerificationResultDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TenantConnections_Verify_WithAUsableStoredProfile_ContactsTheProvider()
    {
        var connectionId = Guid.NewGuid();
        var repo = Substitute.For<IAiConnectionRepository>();
        repo.GetByIdAsync(connectionId, Arg.Any<CancellationToken>())
            .Returns(Connection("OpenAI", secret: "a-key"));
        var driver = ConnectableDriver();
        driver.VerifyAsync(Arg.Any<ProviderEndpoint>(), Arg.Any<CancellationToken>())
            .Returns(new ProviderVerificationResult(AiVerificationStatus.Verified, Summary: "ok", CheckedAt: DateTimeOffset.UtcNow));
        var controller = TenantConnectionsController(repo, tenantAdmin: true, driver);

        Assert.IsType<OkObjectResult>(await controller.Verify(TenantId, connectionId, default));
        await driver.Received(1).VerifyAsync(Arg.Any<ProviderEndpoint>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TenantConnections_PermittedProviders_WithoutTenantAdmin_IsForbidden()
    {
        var repo = Substitute.For<IAiConnectionRepository>();
        var controller = TenantConnectionsController(repo, tenantAdmin: false, ConnectableDriver());

        var result = await controller.GetPermittedProviders(TenantId, default);

        Assert.Equal(StatusCodes.Status403Forbidden, ((ObjectResult)result).StatusCode);
    }

    // The tenant screens name provider families from what the installation loaded, so what they need to render
    // one — the family's own name, the names of the shapes it offers, and the fields each credential shape
    // takes — is answered here rather than held in a table the console shipped with.
    [Fact]
    public async Task TenantConnections_PermittedProviders_DescribesEachFamilyFromItsOwnDeclaration()
    {
        var repo = Substitute.For<IAiConnectionRepository>();
        var controller = TenantConnectionsController(repo, tenantAdmin: true, ConnectableDriver());

        var result = Assert.IsType<OkObjectResult>(await controller.GetPermittedProviders(TenantId, default));
        var payload = Assert.IsType<PermittedProvidersResponse>(result.Value);

        var family = Assert.Single(payload.Providers);
        Assert.Equal("Connectable test family", family.Label);
        Assert.True(family.IsPermitted);
        Assert.False(payload.IsRestricted);

        // This family states none of these names, so each one is read off its member name.
        Assert.Equal(["Auto", "Chat Completions"], family.ProtocolModes.Select(shape => shape.Label));
        Assert.Equal("Api Key", Assert.Single(family.AuthModes).Label);
        Assert.NotEmpty(family.CredentialFields[ConnectableApiKey]);
    }

    // The allow-list editor reads the same answer, and a family the tenant has excluded still has to be listed:
    // it is the box an operator ticks to let it back in.
    [Fact]
    public async Task TenantConnections_PermittedProviders_ListsAFamilyThePolicyExcludes()
    {
        var repo = Substitute.For<IAiConnectionRepository>();
        var controller = TenantConnectionsController(
            repo,
            tenantAdmin: true,
            ConnectableDriver(),
            new TenantProviderPolicy(["meisterdev/azureOpenAi"]));

        var result = Assert.IsType<OkObjectResult>(await controller.GetPermittedProviders(TenantId, default));
        var payload = Assert.IsType<PermittedProvidersResponse>(result.Value);

        Assert.True(payload.IsRestricted);
        Assert.False(Assert.Single(payload.Providers).IsPermitted);
    }

    // A driver that accepts the profile, so a refusal in a test using it came from the controller.
    private static IAiProviderDriver ConnectableDriver()
    {
        var driver = Substitute.For<IAiProviderDriver>();
        driver.Declaration.Returns(ConnectableDeclaration());
        driver.SupportedAuthModes.Returns([ConnectableApiKey]);
        driver.CredentialFields.Returns(ConnectableDeclaration().CredentialFields);
        driver.ValidateProbeTarget(Arg.Any<AiProbeTarget>()).Returns((string?)null);
        return driver;
    }

    // The verify path reads the family's declared reach alongside the connection's base URL, so the driver has to
    // carry a declaration for these tests to exercise the path a configured family takes.
    private static ProviderDeclaration ConnectableDeclaration()
    {
        return new ProviderDeclaration
        {
            Key = ConnectableFamily,
            Label = "Connectable test family",
            Version = "1.0",
            ContractVersion = ProviderContract.Version,
            AuthModes = [new ProviderDeclaredAuthMode(ConnectableApiKey, [AiCredentialFieldSupport.ApiKey])],
            ProtocolModes = new ProviderDeclaredProtocolModes([ProviderDeclaredProtocolModes.Auto, ConnectableFamily + ":ChatCompletions"]),
            ConformanceInputs = new ProviderConformanceInputs(ConnectableApiKey),
        };
    }

    private static AiConnectionDto Connection(string displayName, Guid? tenantId = null, string? secret = null)
    {
        return new AiConnectionDto(
            Guid.NewGuid(),
            null,
            displayName,
            ConnectableFamily,
            "https://example.test",
            ConnectableApiKey,
            AiDiscoveryMode.ManualOnly,
            false,
            [],
            [],
            AiVerificationResultDto.NeverVerified,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            null,
            secret,
            tenantId ?? TenantId);
    }

    private static LogicalModelDto Dto(string name)
    {
        return new LogicalModelDto(
            Guid.NewGuid(), name, AiOperationKind.Chat, Guid.NewGuid(), Guid.NewGuid(), ReviewReasoningEffort.None, ProviderDeclaredProtocolModes.Auto);
    }

    private static ClientLogicalModelsController ClientController(ILogicalModelCatalogRepository catalog, bool clientAdmin)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["UserId"] = Guid.NewGuid().ToString();
        if (clientAdmin)
        {
            ctx.Items["ClientRoles"] = new Dictionary<Guid, ClientRole> { [ClientId] = ClientRole.ClientAdministrator };
        }

        return new ClientLogicalModelsController(catalog) { ControllerContext = new ControllerContext { HttpContext = ctx } };
    }

    private static TenantLogicalModelsController TenantController(ILogicalModelCatalogRepository catalog, bool tenantAdmin)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["UserId"] = Guid.NewGuid().ToString();
        if (tenantAdmin)
        {
            ctx.Items["TenantRoles"] = new Dictionary<Guid, TenantRole> { [TenantId] = TenantRole.TenantAdministrator };
        }

        return new TenantLogicalModelsController(catalog) { ControllerContext = new ControllerContext { HttpContext = ctx } };
    }

    private static TenantAiConnectionsController TenantConnectionsController(
        IAiConnectionRepository connections,
        bool tenantAdmin,
        IAiProviderDriver? driver = null,
        TenantProviderPolicy? policy = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["UserId"] = Guid.NewGuid().ToString();
        if (tenantAdmin)
        {
            ctx.Items["TenantRoles"] = new Dictionary<Guid, TenantRole> { [TenantId] = TenantRole.TenantAdministrator };
        }

        var registry = Substitute.For<IAiProviderDriverRegistry>();
        if (driver is not null)
        {
            // Read before the registry call is set up: a substitute's property read inside Returns() would be
            // the call the return value is attached to.
            var registered = driver.Declaration.Key;

            registry.RegisteredKinds.Returns([registered]);
            registry.GetRequired(Arg.Any<string>()).Returns(driver);
            registry.IsRegistered(Arg.Any<string>()).Returns(true);
        }

        var policies = Substitute.For<ITenantProviderPolicyProvider>();
        policies.GetForTenantAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(policy ?? TenantProviderPolicy.Unrestricted);

        return new TenantAiConnectionsController(connections, registry, policies, EgressUrlPolicy.Locked)
        {
            ControllerContext = new ControllerContext { HttpContext = ctx },
        };
    }
}
