// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Controllers;

/// <summary>
///     Who may read and change which tenant's runners. The registry is the one place an operator hands a
///     host the right to fetch source and run reviews, so the interesting cases are all the ones where a
///     caller names something that is not theirs.
/// </summary>
public sealed class AdminRunnersControllerAuthorizationTests
{
    private static readonly Guid OwningTenant = Guid.Parse("aaaaaaaa-1111-4111-8111-111111111111");
    private static readonly Guid OtherTenant = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task List_TenantAdministratorOfThatTenant_ReadsIt()
    {
        var registry = CreateRegistry();
        var controller = CreateController(registry, tenantRoles: TenantAdminOf(OwningTenant));

        var result = await controller.List(OwningTenant, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task List_TenantAdministratorOfAnotherTenant_IsRefused()
    {
        var registry = CreateRegistry();
        var controller = CreateController(registry, tenantRoles: TenantAdminOf(OtherTenant));

        var result = await controller.List(OwningTenant, CancellationToken.None);

        AssertForbidden(result);
        await registry.DidNotReceive().ListAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_PlatformAdministrator_ReadsAnyTenant()
    {
        var registry = CreateRegistry();
        var controller = CreateController(registry, isAdmin: true);

        var result = await controller.List(OwningTenant, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task List_TenantView_WithholdsTheInstallationWideStall()
    {
        var fleet = Substitute.For<IRunnerFleetMonitor>();
        fleet.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(StalledStatus());

        var controller = CreateController(CreateRegistry(), fleet: fleet, tenantRoles: TenantAdminOf(OwningTenant));

        var result = await controller.List(OwningTenant, CancellationToken.None);

        var payload = Assert.IsType<RunnerRegistryDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Null(payload.Stall);
    }

    [Fact]
    public async Task ListAll_PlatformAdministrator_SpansEveryTenantAndNamesThem()
    {
        var registry = CreateRegistry();
        registry.ListAsync(OwningTenant, Arg.Any<CancellationToken>())
            .Returns([CreateRunner(OwningTenant)]);
        registry.ListAsync(OtherTenant, Arg.Any<CancellationToken>())
            .Returns([CreateRunner(OtherTenant)]);

        var controller = CreateController(registry, isAdmin: true, tenantsInInstallation: [OwningTenant, OtherTenant]);

        var result = await controller.ListAll(CancellationToken.None);

        var payload = Assert.IsType<RunnerRegistryDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(2, payload.Runners.Count);
        Assert.Contains(payload.Runners, runner => runner.TenantId == OwningTenant && runner.TenantName == "tenant-" + OwningTenant);
        Assert.Contains(payload.Runners, runner => runner.TenantId == OtherTenant);
    }

    [Fact]
    public async Task ListAll_TenantAdministrator_IsRefused()
    {
        // Administering one tenant is not grounds for reading the rest, so no tenant role reaches this view.
        var controller = CreateController(CreateRegistry(), tenantRoles: TenantAdminOf(OwningTenant));

        var result = await controller.ListAll(CancellationToken.None);

        AssertForbidden(result);
    }

    [Fact]
    public async Task IssueToken_ForAnotherTenant_IsRefused()
    {
        var runners = Substitute.For<IRunnerRegistrationService>();
        var controller = CreateController(CreateRegistry(), runners: runners, tenantRoles: TenantAdminOf(OtherTenant));

        var result = await controller.IssueToken(
            new IssueRunnerTokenRequest { TenantId = OwningTenant, ClientScope = [], ValidForHours = 1 },
            CancellationToken.None);

        AssertForbidden(result);
        await runners.DidNotReceive().IssueRegistrationTokenAsync(
            Arg.Any<Guid>(),
            Arg.Any<IReadOnlyList<Guid>>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<Guid>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // A System-tenant runner is offered every tenant's work, so enrolling one gives a host the right to fetch
    // any customer's source. That stays a platform decision, including for a caller holding the System
    // tenant's own administrator role.
    [Fact]
    public async Task IssueToken_ForTheSystemTenant_RefusesEvenItsOwnTenantAdministrator()
    {
        var runners = Substitute.For<IRunnerRegistrationService>();
        var controller = CreateController(
            CreateRegistry(),
            runners: runners,
            tenantRoles: TenantAdminOf(TenantCatalog.SystemTenantId));

        var result = await controller.IssueToken(
            new IssueRunnerTokenRequest { TenantId = TenantCatalog.SystemTenantId, ClientScope = [], ValidForHours = 1 },
            CancellationToken.None);

        AssertForbidden(result);
        await runners.DidNotReceive().IssueRegistrationTokenAsync(
            Arg.Any<Guid>(),
            Arg.Any<IReadOnlyList<Guid>>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<Guid>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // A shared runner may be scoped across tenants, so the foreign-client refusal does not apply to it.
    // Otherwise it would be the only runner that cannot be narrowed to particular clients.
    [Fact]
    public async Task AssignScope_SystemRunner_MayNameAnyTenantsClients()
    {
        var runner = CreateRunner(TenantCatalog.SystemTenantId);
        var clientOfSomeTenant = Guid.NewGuid();

        var registry = CreateRegistry();
        registry.FindByIdAsync(runner.Id, Arg.Any<CancellationToken>()).Returns(runner);

        var clients = Substitute.For<IClientAdminService>();
        clients.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([CreateClient(clientOfSomeTenant, OtherTenant)]);

        var runners = Substitute.For<IRunnerRegistrationService>();
        runners.AssignClientScopeAsync(runner.Id, Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var controller = CreateController(registry, runners: runners, clients: clients, isAdmin: true);

        var result = await controller.AssignScope(
            runner.Id,
            new AssignRunnerScopeRequest { ClientScope = [clientOfSomeTenant] },
            CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task Revoke_RunnerOfAnotherTenant_IsRefused()
    {
        var runner = CreateRunner(OwningTenant);
        var registry = CreateRegistry();
        registry.FindByIdAsync(runner.Id, Arg.Any<CancellationToken>()).Returns(runner);

        var runners = Substitute.For<IRunnerRegistrationService>();
        var controller = CreateController(registry, runners: runners, tenantRoles: TenantAdminOf(OtherTenant));

        var result = await controller.Revoke(runner.Id, CancellationToken.None);

        AssertForbidden(result);
        await runners.DidNotReceive().RevokeAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_RunnerOfAnotherTenant_IsRefused()
    {
        var runner = CreateRunner(OwningTenant);
        var registry = CreateRegistry();
        registry.FindByIdAsync(runner.Id, Arg.Any<CancellationToken>()).Returns(runner);

        var runners = Substitute.For<IRunnerRegistrationService>();
        var controller = CreateController(registry, runners: runners, tenantRoles: TenantAdminOf(OtherTenant));

        var result = await controller.Delete(runner.Id, CancellationToken.None);

        AssertForbidden(result);
        await runners.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_RunnerOfTheirOwnTenant_IsCarriedOut()
    {
        var runner = CreateRunner(OwningTenant);
        var registry = CreateRegistry();
        registry.FindByIdAsync(runner.Id, Arg.Any<CancellationToken>()).Returns(runner);

        var runners = Substitute.For<IRunnerRegistrationService>();
        runners.DeleteAsync(runner.Id, Arg.Any<CancellationToken>()).Returns(RunnerDeletionOutcome.Deleted);

        var controller = CreateController(registry, runners: runners, tenantRoles: TenantAdminOf(OwningTenant));

        var result = await controller.Delete(runner.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task RevokeToken_OfAnotherTenant_IsRefused()
    {
        var token = CreateToken(OwningTenant);
        var registry = CreateRegistry();
        registry.FindTokenByIdAsync(token.Id, Arg.Any<CancellationToken>()).Returns(token);

        var runners = Substitute.For<IRunnerRegistrationService>();
        var controller = CreateController(registry, runners: runners, tenantRoles: TenantAdminOf(OtherTenant));

        var result = await controller.RevokeToken(token.Id, CancellationToken.None);

        AssertForbidden(result);
        await runners.DidNotReceive().RevokeRegistrationTokenAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssignScope_RunnerOfAnotherTenant_IsRefused()
    {
        var runner = CreateRunner(OwningTenant);
        var registry = CreateRegistry();
        registry.FindByIdAsync(runner.Id, Arg.Any<CancellationToken>()).Returns(runner);

        var runners = Substitute.For<IRunnerRegistrationService>();
        var controller = CreateController(registry, runners: runners, tenantRoles: TenantAdminOf(OtherTenant));

        var result = await controller.AssignScope(
            runner.Id,
            new AssignRunnerScopeRequest { ClientScope = [] },
            CancellationToken.None);

        AssertForbidden(result);
        await runners.DidNotReceive().AssignClientScopeAsync(
            Arg.Any<Guid>(),
            Arg.Any<IReadOnlyList<Guid>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssignScope_ClientOfAnotherTenant_IsRefusedEvenForAPlatformAdministrator()
    {
        // The lease offer joins the runner's tenant to the client's before it consults the stamped scope, so
        // a foreign client would be stored and then never matched. Refusing reports that, rather than leaving
        // a scope that reads as set and routes nothing.
        var runner = CreateRunner(OwningTenant);
        var foreignClient = Guid.NewGuid();

        var registry = CreateRegistry();
        registry.FindByIdAsync(runner.Id, Arg.Any<CancellationToken>()).Returns(runner);

        var clients = Substitute.For<IClientAdminService>();
        clients.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([CreateClient(foreignClient, OtherTenant)]);

        var runners = Substitute.For<IRunnerRegistrationService>();
        var controller = CreateController(registry, runners: runners, clients: clients, isAdmin: true);

        var result = await controller.AssignScope(
            runner.Id,
            new AssignRunnerScopeRequest { ClientScope = [foreignClient] },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        await runners.DidNotReceive().AssignClientScopeAsync(
            Arg.Any<Guid>(),
            Arg.Any<IReadOnlyList<Guid>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssignScope_ClientOfTheOwningTenant_IsAccepted()
    {
        var runner = CreateRunner(OwningTenant);
        var ownClient = Guid.NewGuid();

        var registry = CreateRegistry();
        registry.FindByIdAsync(runner.Id, Arg.Any<CancellationToken>()).Returns(runner);

        var clients = Substitute.For<IClientAdminService>();
        clients.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([CreateClient(ownClient, OwningTenant)]);

        var runners = Substitute.For<IRunnerRegistrationService>();
        runners.AssignClientScopeAsync(runner.Id, Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var controller = CreateController(
            registry,
            runners: runners,
            clients: clients,
            tenantRoles: TenantAdminOf(OwningTenant));

        var result = await controller.AssignScope(
            runner.Id,
            new AssignRunnerScopeRequest { ClientScope = [ownClient] },
            CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task Revoke_UnknownRunner_IsNotFound()
    {
        var registry = CreateRegistry();
        registry.FindByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((ReviewRunner?)null);

        var controller = CreateController(registry, isAdmin: true);

        var result = await controller.Revoke(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task List_CallerWithNoAdministrativeRoleAtAll_IsRefused()
    {
        var controller = CreateController(
            CreateRegistry(), tenantRoles: new Dictionary<Guid, TenantRole>
            {
                [OwningTenant] = TenantRole.TenantUser,
            });

        var result = await controller.List(OwningTenant, CancellationToken.None);

        AssertForbidden(result);
    }

    private static Dictionary<Guid, TenantRole> TenantAdminOf(Guid tenantId)
    {
        return new Dictionary<Guid, TenantRole> { [tenantId] = TenantRole.TenantAdministrator };
    }

    private static void AssertForbidden(IActionResult result)
    {
        var status = Assert.IsAssignableFrom<IStatusCodeActionResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
    }

    private static IRunnerRegistry CreateRegistry()
    {
        var registry = Substitute.For<IRunnerRegistry>();
        registry.ListAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);
        registry.ListTokensAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);
        return registry;
    }

    private static ReviewRunner CreateRunner(Guid tenantId)
    {
        return new ReviewRunner(
            Guid.NewGuid(),
            tenantId,
            "runner",
            [],
            RunnerContractVersionForTests,
            "hash",
            "lookup",
            DateTimeOffset.UtcNow.AddDays(30),
            DateTimeOffset.UtcNow);
    }

    private static RunnerRegistrationToken CreateToken(Guid tenantId)
    {
        return new RunnerRegistrationToken(
            Guid.NewGuid(),
            tenantId,
            [],
            "hash",
            "lookup",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(1),
            1,
            Guid.NewGuid());
    }

    private static ClientDto CreateClient(Guid clientId, Guid tenantId)
    {
        return new ClientDto(
            clientId,
            "client",
            true,
            DateTimeOffset.UtcNow,
            CommentResolutionBehavior.Silent,
            null,
            null,
            null,
            true,
            TenantId: tenantId);
    }

    private const int RunnerContractVersionForTests = 2;

    private static AdminRunnersController CreateController(
        IRunnerRegistry registry,
        IRunnerRegistrationService? runners = null,
        IRunnerFleetMonitor? fleet = null,
        IClientAdminService? clients = null,
        bool isAdmin = false,
        IReadOnlyDictionary<Guid, TenantRole>? tenantRoles = null,
        IReadOnlyList<Guid>? tenantsInInstallation = null)
    {
        var tenants = Substitute.For<ITenantAdminService>();
        tenants.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns((tenantsInInstallation ?? []).Select(CreateTenant).ToList());
        tenants.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => CreateTenant(call.Arg<Guid>()));

        var controller = new AdminRunnersController(
            runners ?? Substitute.For<IRunnerRegistrationService>(),
            registry,
            fleet,
            null,
            null,
            null,
            clients,
            tenants)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        controller.HttpContext.Items["UserId"] = Guid.NewGuid().ToString();

        if (isAdmin)
        {
            controller.HttpContext.Items["IsAdmin"] = true;
        }

        controller.HttpContext.Items["TenantRoles"] = tenantRoles is null
            ? new Dictionary<Guid, TenantRole>()
            : new Dictionary<Guid, TenantRole>(tenantRoles);

        return controller;
    }

    private static TenantDto CreateTenant(Guid tenantId)
    {
        return new TenantDto(
            tenantId,
            "slug-" + tenantId,
            "tenant-" + tenantId,
            true,
            true,
            true,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }

    private static RunnerFleetStatus StalledStatus()
    {
        return new RunnerFleetStatus(
            ReviewExecutionMode.RunnersOnly,
            1,
            new QueueStallCondition(QueueStallCause.NoActiveRunner, 7, DateTimeOffset.UtcNow.AddMinutes(-30), "detail"));
    }
}
