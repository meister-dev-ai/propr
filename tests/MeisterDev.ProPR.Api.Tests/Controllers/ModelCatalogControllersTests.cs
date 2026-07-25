// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterDev.ProPR.Api.Features.Clients.Controllers;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Controllers;

/// <summary>
///     Covers the three model-catalog surfaces and, above all, the boundary each one sits behind: a client
///     browses, a tenant administrator sets its own negotiated pricing, and only a platform administrator may
///     replace the global snapshot every tenant reads.
/// </summary>
public sealed class ModelCatalogControllersTests
{
    private static readonly Guid ClientId = Guid.Parse("cccccccc-0000-0000-0000-00000000000c");
    private static readonly Guid TenantId = Guid.Parse("11111111-0000-0000-0000-000000000011");

    [Fact]
    public async Task ClientCatalog_ReturnsEffectiveEntriesForThatClient()
    {
        var catalog = Substitute.For<IModelCatalogRepository>();
        catalog.GetEffectiveForClientAsync(ClientId, "deepseek", Arg.Any<CancellationToken>())
            .Returns([Entry("deepseek-reasoner", 4m, AiModelCatalogLayer.TenantOverride)]);

        var result = await ClientController(catalog, clientAdmin: true).GetModels(ClientId, "deepseek");

        var entries = Assert.IsAssignableFrom<IReadOnlyList<AiModelCatalogEntryDto>>(Assert.IsType<OkObjectResult>(result).Value);
        var entry = Assert.Single(entries);
        Assert.Equal("deepseek-reasoner", entry.RemoteModelId);
        // The layer travels to the surface so a negotiated rate can be shown as negotiated.
        Assert.Equal(AiModelCatalogLayer.TenantOverride, entry.PricingLayer);
    }

    [Fact]
    public async Task ClientCatalog_RequiresClientAdministrator()
    {
        var catalog = Substitute.For<IModelCatalogRepository>();

        var result = await ClientController(catalog, clientAdmin: false).GetModels(ClientId);

        Assert.IsNotType<OkObjectResult>(result);
        await catalog.DidNotReceiveWithAnyArgs().GetEffectiveForClientAsync(default, default, default);
    }

    [Fact]
    public async Task TenantOverride_IsStoredForTheTenantInTheRoute()
    {
        var catalog = Substitute.For<IModelCatalogRepository>();
        var request = new AiModelCatalogOverrideDto("deepseek", "deepseek-reasoner", InputCostPer1MUsd: 0.2m);

        var result = await TenantController(catalog, tenantAdmin: true).UpsertOverride(TenantId, request);

        Assert.IsType<NoContentResult>(result);
        await catalog.Received(1).UpsertTenantOverrideAsync(TenantId, request, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TenantOverride_RequiresTenantAdministrator()
    {
        var catalog = Substitute.For<IModelCatalogRepository>();

        var result = await TenantController(catalog, tenantAdmin: false)
            .UpsertOverride(TenantId, new AiModelCatalogOverrideDto("p", "m", InputCostPer1MUsd: 1m));

        Assert.IsNotType<NoContentResult>(result);
        await catalog.DidNotReceiveWithAnyArgs().UpsertTenantOverrideAsync(default, default!, default);
    }

    // A negative price would silently invert a cost cap, so it is rejected rather than stored.
    [Fact]
    public async Task TenantOverride_RejectsANegativePrice()
    {
        var catalog = Substitute.For<IModelCatalogRepository>();

        var result = await TenantController(catalog, tenantAdmin: true)
            .UpsertOverride(TenantId, new AiModelCatalogOverrideDto("p", "m", InputCostPer1MUsd: -1m));

        Assert.IsType<ObjectResult>(result);
        await catalog.DidNotReceiveWithAnyArgs().UpsertTenantOverrideAsync(default, default!, default);
    }

    [Fact]
    public async Task TenantOverride_RequiresAProviderAndModel()
    {
        var catalog = Substitute.For<IModelCatalogRepository>();

        var result = await TenantController(catalog, tenantAdmin: true)
            .UpsertOverride(TenantId, new AiModelCatalogOverrideDto(" ", " ", InputCostPer1MUsd: 1m));

        Assert.IsType<ObjectResult>(result);
        await catalog.DidNotReceiveWithAnyArgs().UpsertTenantOverrideAsync(default, default!, default);
    }

    [Fact]
    public async Task DeletingAnAbsentOverride_IsNotFound()
    {
        var catalog = Substitute.For<IModelCatalogRepository>();
        catalog.DeleteTenantOverrideAsync(TenantId, "p", "m", Arg.Any<CancellationToken>()).Returns(false);

        var result = await TenantController(catalog, tenantAdmin: true).DeleteOverride(TenantId, "p", "m");

        Assert.IsType<NotFoundResult>(result);
    }

    // Import writes the rows every tenant reads, which is why a tenant administrator must not be able to do it.
    [Fact]
    public async Task SnapshotImport_RequiresAPlatformAdministrator()
    {
        var import = Substitute.For<IModelCatalogImportService>();

        var result = await AdminController(import, platformAdmin: false).ImportSnapshot(File("{}"));

        Assert.IsNotType<OkObjectResult>(result);
        await import.DidNotReceiveWithAnyArgs().ImportSnapshotAsync(default!, default);
    }

    [Fact]
    public async Task SnapshotImport_ReportsHowManyEntriesWereWritten()
    {
        var import = Substitute.For<IModelCatalogImportService>();
        import.ImportSnapshotAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(37);

        var result = await AdminController(import, platformAdmin: true).ImportSnapshot(File("{}"));

        var response = Assert.IsType<ModelCatalogImportResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(37, response.EntriesWritten);
    }

    [Fact]
    public async Task SnapshotImport_RequiresAFile()
    {
        var import = Substitute.For<IModelCatalogImportService>();

        var result = await AdminController(import, platformAdmin: true).ImportSnapshot(null);

        Assert.IsType<ObjectResult>(result);
        await import.DidNotReceiveWithAnyArgs().ImportSnapshotAsync(default!, default);
    }

    // A malformed upload is operator error: it must come back as a validation problem naming the cause, not as a
    // 500 that only the log explains.
    [Fact]
    public async Task SnapshotImport_ReportsAMalformedUploadAsAValidationProblem()
    {
        var import = Substitute.For<IModelCatalogImportService>();
        import.ImportSnapshotAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns<Task<int>>(_ => throw new System.Text.Json.JsonException("unexpected token"));

        var result = await AdminController(import, platformAdmin: true).ImportSnapshot(File("not json"));

        Assert.IsType<ObjectResult>(result);
    }

    private static AiModelCatalogEntryDto Entry(string remoteModelId, decimal input, AiModelCatalogLayer layer) =>
        new(
            "deepseek",
            "DeepSeek",
            remoteModelId,
            remoteModelId,
            null,
            true,
            true,
            true,
            false,
            null,
            131072,
            65536,
            input,
            null,
            null,
            null,
            false,
            null,
            layer);

    private static IFormFile File(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "snapshot", "snapshot.json");
    }

    private static ClientModelCatalogController ClientController(IModelCatalogRepository catalog, bool clientAdmin)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["UserId"] = Guid.NewGuid().ToString();
        if (clientAdmin)
        {
            ctx.Items["ClientRoles"] = new Dictionary<Guid, ClientRole> { [ClientId] = ClientRole.ClientAdministrator };
        }

        return new ClientModelCatalogController(catalog) { ControllerContext = new ControllerContext { HttpContext = ctx } };
    }

    private static TenantModelCatalogController TenantController(IModelCatalogRepository catalog, bool tenantAdmin)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["UserId"] = Guid.NewGuid().ToString();
        if (tenantAdmin)
        {
            ctx.Items["TenantRoles"] = new Dictionary<Guid, TenantRole> { [TenantId] = TenantRole.TenantAdministrator };
        }

        return new TenantModelCatalogController(catalog) { ControllerContext = new ControllerContext { HttpContext = ctx } };
    }

    private static AdminModelCatalogController AdminController(IModelCatalogImportService import, bool platformAdmin)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["UserId"] = Guid.NewGuid().ToString();
        ctx.Items["IsAdmin"] = platformAdmin;

        return new AdminModelCatalogController(import, NullLogger<AdminModelCatalogController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = ctx },
        };
    }
}
