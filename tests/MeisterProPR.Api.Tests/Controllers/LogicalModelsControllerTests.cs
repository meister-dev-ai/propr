// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Controllers;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.AI.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Controllers;

public sealed class LogicalModelsControllerTests
{
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

    private static AiConnectionDto Connection(string displayName, Guid? tenantId = null)
    {
        return new AiConnectionDto(
            Guid.NewGuid(),
            null,
            displayName,
            AiProviderKind.OpenAi,
            "https://example.test",
            AiAuthMode.ApiKey,
            AiDiscoveryMode.ManualOnly,
            false,
            [],
            [],
            AiVerificationResultDto.NeverVerified,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            tenantId ?? TenantId);
    }

    private static LogicalModelDto Dto(string name)
    {
        return new LogicalModelDto(Guid.NewGuid(), name, AiOperationKind.Chat, Guid.NewGuid(), Guid.NewGuid(), ReviewReasoningEffort.None, AiProtocolMode.Auto);
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

    private static TenantAiConnectionsController TenantConnectionsController(IAiConnectionRepository connections, bool tenantAdmin)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["UserId"] = Guid.NewGuid().ToString();
        if (tenantAdmin)
        {
            ctx.Items["TenantRoles"] = new Dictionary<Guid, TenantRole> { [TenantId] = TenantRole.TenantAdministrator };
        }

        return new TenantAiConnectionsController(connections, Substitute.For<IAiProviderDriverRegistry>())
        {
            ControllerContext = new ControllerContext { HttpContext = ctx },
        };
    }
}
