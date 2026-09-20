// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.AddIns;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Controllers;

/// <summary>
///     What an administrator can read about the add-ins this host loaded, and who may read it.
/// </summary>
public sealed class AdminAiProviderAddInsControllerTests
{
    [Fact]
    public void ALoadedFamilyIsReportedWithEveryValueTheLoaderRecorded()
    {
        var result = Assert.IsType<OkObjectResult>(Controller(Catalog()).GetAddIns());
        var inventory = Assert.IsType<ProviderAddInInventoryDto>(result.Value);

        var family = Assert.Single(inventory.Loaded);
        Assert.Equal("meisterdev/example", family.Key);
        Assert.Equal("Example", family.Label);
        Assert.Equal("2.1", family.Version);
        Assert.Equal("1.0", family.ContractVersion);
        Assert.Equal(["api.example.com"], family.ReachedHostPatterns);
        Assert.Equal("example-connections", family.RequiredCapabilityKey);
        Assert.Equal("/app/provider-add-ins/example/example.dll", family.FilePath);
        Assert.Equal("abc123", family.ContentHash);
        Assert.Equal("built-in", family.Origin);
    }

    [Fact]
    public void ARejectedAssemblyIsReportedWithItsCategoryItsReasonAndItsFile()
    {
        var result = Assert.IsType<OkObjectResult>(Controller(Catalog()).GetAddIns());
        var inventory = Assert.IsType<ProviderAddInInventoryDto>(result.Value);

        var skipped = Assert.Single(inventory.Rejected);
        Assert.Equal("mis-packaged", skipped.Category);
        Assert.Equal("It ships its own copy of the contract.", skipped.Reason);
        Assert.Equal("/plugins/broken/broken.dll", skipped.FilePath);
        Assert.Equal("def456", skipped.ContentHash);
        Assert.Equal("external", skipped.Origin);
    }

    // Two families may declare one label, so the key is what tells them apart and both are listed.
    [Fact]
    public void TwoFamiliesSharingALabelBothAppearUnderTheirOwnKeys()
    {
        var catalog = new ProviderAddInCatalog(
            [
                Loaded("meisterdev/first", "Shared label"),
                Loaded("meisterdev/second", "Shared label"),
            ],
            [],
            [],
            []);

        var result = Assert.IsType<OkObjectResult>(Controller(catalog).GetAddIns());
        var inventory = Assert.IsType<ProviderAddInInventoryDto>(result.Value);

        Assert.Equal(["meisterdev/first", "meisterdev/second"], inventory.Loaded.Select(family => family.Key));
        Assert.All(inventory.Loaded, family => Assert.Equal("Shared label", family.Label));
    }

    // The directories, the file paths and the hashes describe the installation, so administering one tenant is
    // not grounds for reading them.
    [Fact]
    public void ACallerWhoIsNotAPlatformAdministratorIsRefused()
    {
        var controller = new AdminAiProviderAddInsController(Catalog(), Activations(Catalog()))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        Assert.IsNotType<OkObjectResult>(controller.GetAddIns());
    }

    private static AdminAiProviderAddInsController Controller(ProviderAddInCatalog catalog)
    {
        var controller = new AdminAiProviderAddInsController(catalog, Activations(catalog))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.HttpContext.Items["IsAdmin"] = true;

        return controller;
    }

    // The inventory read touches none of it. The activation endpoints are covered where a database is, because
    // activating writes a row and loads an assembly, neither of which a controller test can stand in for.
    private static ProviderAddInActivationService Activations(ProviderAddInCatalog catalog)
    {
        return new ProviderAddInActivationService(
            catalog,
            new ProviderAddInActivations([]),
            new AiProviderRegistry([]),
            Substitute.For<IProviderConnectionOwnerRoles>(),
            Substitute.For<IDbContextFactory<MeisterProPRDbContext>>(),
            TimeProvider.System,
            NullLogger<ProviderAddInActivationService>.Instance);
    }

    private static ProviderAddInCatalog Catalog()
    {
        return new ProviderAddInCatalog(
            [Loaded("meisterdev/example", "Example")],
            [
                new RejectedProviderAddIn(
                    ProviderAddInRejectionCategory.MisPackaged,
                    "It ships its own copy of the contract.",
                    "/plugins/broken/broken.dll",
                    "def456",
                    null,
                    ProviderAddInOrigin.External),
            ],
            [],
            []);
    }

    private static LoadedProviderAddIn Loaded(string key, string label)
    {
        return new LoadedProviderAddIn(
            key,
            label,
            "2.1",
            "1.0",
            ["api.example.com"],
            "example-connections",
            "/app/provider-add-ins/example/example.dll",
            "abc123",
            ProviderAddInOrigin.BuiltIn);
    }
}
