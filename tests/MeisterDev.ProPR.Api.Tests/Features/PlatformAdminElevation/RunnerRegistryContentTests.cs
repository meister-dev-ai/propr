// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.PlatformAdminElevation;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Runner.Contracts;
using MeisterDev.ProPR.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Features.PlatformAdminElevation;

/// <summary>
///     The runner registry is metadata, except for the title of the pull request each runner reviews. A platform
///     administrator reads that title only for a client an elevation covers, and the tenant administrators read the
///     titles of their own tenant. The elevation covers one client of Acme. Acme and the second tenant, Globex, each
///     have one runner, and the runner of Globex reviews a pull request of a client of Globex.
/// </summary>
public sealed class RunnerRegistryContentTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-2222-4222-8222-222222222222");
    private static readonly Guid SecondTenant = Guid.Parse("bbbbbbbb-3333-4333-8333-333333333333");
    private static readonly Guid ElevatedClient = Guid.NewGuid();
    private static readonly Guid OtherClient = Guid.NewGuid();
    private static readonly Guid SecondTenantClient = Guid.NewGuid();
    private static readonly Guid ElevatedJob = Guid.NewGuid();
    private static readonly Guid OtherJob = Guid.NewGuid();
    private static readonly Guid SecondTenantJob = Guid.NewGuid();

    private static readonly PlatformAdminElevationDto Elevation = new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "operator",
        Tenant,
        "Acme",
        ElevatedClient,
        "Payments",
        "TICKET-15",
        DateTimeOffset.UtcNow.AddMinutes(-5),
        DateTimeOffset.UtcNow.AddMinutes(55),
        null,
        null,
        PlatformAdminElevationState.Active,
        0);

    [Fact]
    public async Task APlatformAdministratorReadsTheTitlesOfTheClientsAnElevationCoversOnly()
    {
        var elevations = ElevationService();
        var controller = CreateController(isAdmin: true, elevations);

        var jobs = Executing(Assert.IsType<RunnerRegistryDto>(Assert.IsType<OkObjectResult>(await controller.ListAll()).Value));

        var elevated = Assert.Single(jobs, job => job.JobId == ElevatedJob);
        Assert.Equal("Fix the payment rounding", elevated.Title);
        Assert.False(elevated.ContentWithheld);
        var other = Assert.Single(jobs, job => job.JobId == OtherJob);
        Assert.Null(other.Title);
        Assert.True(other.ContentWithheld);
        Assert.Equal("billing", other.RepositoryName);
        Assert.Equal(7, other.PullRequestNumber);
        var secondTenant = Assert.Single(jobs, job => job.JobId == SecondTenantJob);
        Assert.Null(secondTenant.Title);
        Assert.True(secondTenant.ContentWithheld);
        Assert.True(PlatformAdminContent.HasPendingReads(controller.HttpContext));
    }

    [Fact]
    public async Task TheTenantViewOfAPlatformAdministratorWithholdsTheTitlesAlike()
    {
        var controller = CreateController(isAdmin: true, ElevationService());

        var jobs = Executing(Assert.IsType<RunnerRegistryDto>(Assert.IsType<OkObjectResult>(await controller.List(Tenant)).Value));

        Assert.Equal([ElevatedJob, OtherJob], jobs.Select(job => job.JobId));
        var other = Assert.Single(jobs, job => job.JobId == OtherJob);
        Assert.Null(other.Title);
        Assert.True(other.ContentWithheld);
        var elevated = Assert.Single(jobs, job => job.JobId == ElevatedJob);
        Assert.Equal("Fix the payment rounding", elevated.Title);
        Assert.False(elevated.ContentWithheld);

        var secondTenantJobs = Executing(Assert.IsType<RunnerRegistryDto>(Assert.IsType<OkObjectResult>(await controller.List(SecondTenant)).Value));

        var secondTenant = Assert.Single(secondTenantJobs);
        Assert.Equal(SecondTenantJob, secondTenant.JobId);
        Assert.Null(secondTenant.Title);
        Assert.True(secondTenant.ContentWithheld);
    }

    [Fact]
    public async Task ATenantAdministratorReadsTheTitlesOfTheirTenantWithoutTheElevationRule()
    {
        var elevations = ElevationService();
        var controller = CreateController(isAdmin: false, elevations);

        var jobs = Executing(Assert.IsType<RunnerRegistryDto>(Assert.IsType<OkObjectResult>(await controller.List(Tenant)).Value));

        Assert.Equal([ElevatedJob, OtherJob], jobs.Select(job => job.JobId));
        Assert.All(jobs, job => Assert.False(job.ContentWithheld));
        Assert.Equal("Rename the ledger", Assert.Single(jobs, job => job.JobId == OtherJob).Title);
        await elevations.DidNotReceiveWithAnyArgs().ResolveAsync(default, default!, default);
        Assert.False(PlatformAdminContent.HasPendingReads(controller.HttpContext));
    }

    private static List<RunnerJobDto> Executing(RunnerRegistryDto registry) =>
        [.. registry.Runners.SelectMany(runner => runner.Executing)];

    private static IPlatformAdminElevationService ElevationService()
    {
        var clients = new Dictionary<Guid, (string Name, Guid TenantId)>
        {
            [ElevatedClient] = ("Payments", Tenant),
            [OtherClient] = ("Ledger", Tenant),
            [SecondTenantClient] = ("Invoices", SecondTenant),
        };

        var elevations = Substitute.For<IPlatformAdminElevationService>();
        elevations.ResolveAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyCollection<Guid>>()
                .Select(clientId => clientId == ElevatedClient
                    ? new ContentAccessGrant(clientId, clients[clientId].Name, Tenant, ContentAccessKind.Elevation, Elevation)
                    : new ContentAccessGrant(clientId, clients[clientId].Name, clients[clientId].TenantId, ContentAccessKind.Refused))
                .ToList());
        return elevations;
    }

    private static AdminRunnersController CreateController(bool isAdmin, IPlatformAdminElevationService elevations)
    {
        var runner = CreateRunner(Tenant);
        var secondTenantRunner = CreateRunner(SecondTenant);

        var registry = Substitute.For<IRunnerRegistry>();
        registry.ListAsync(Tenant, Arg.Any<CancellationToken>()).Returns([runner]);
        registry.ListAsync(SecondTenant, Arg.Any<CancellationToken>()).Returns([secondTenantRunner]);
        registry.ListTokensAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);

        var workload = Substitute.For<IRunnerWorkloadReader>();
        workload.GetWorkloadAsync(Tenant, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(
                Workload(
                    runner,
                    new RunnerExecutingJob(ElevatedJob, ElevatedClient, "payments", 3, "Fix the payment rounding", DateTimeOffset.UtcNow, 0),
                    new RunnerExecutingJob(OtherJob, OtherClient, "billing", 7, "Rename the ledger", DateTimeOffset.UtcNow, 0)));
        workload.GetWorkloadAsync(SecondTenant, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(
                Workload(
                    secondTenantRunner,
                    new RunnerExecutingJob(SecondTenantJob, SecondTenantClient, "invoices", 11, "Round the invoice totals", DateTimeOffset.UtcNow, 0)));

        var tenants = Substitute.For<ITenantAdminService>();
        tenants.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([CreateTenant(Tenant, "acme", "Acme"), CreateTenant(SecondTenant, "globex", "Globex")]);
        tenants.GetByIdAsync(Tenant, Arg.Any<CancellationToken>()).Returns(CreateTenant(Tenant, "acme", "Acme"));
        tenants.GetByIdAsync(SecondTenant, Arg.Any<CancellationToken>()).Returns(CreateTenant(SecondTenant, "globex", "Globex"));

        var controller = new AdminRunnersController(
            runners: Substitute.For<IRunnerRegistrationService>(),
            registry: registry,
            workload: workload,
            tenants: tenants)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = new ServiceCollection().AddSingleton(elevations).BuildServiceProvider(),
                },
            },
        };

        controller.HttpContext.Items["UserId"] = Guid.NewGuid().ToString();
        controller.HttpContext.Items["TenantRoles"] = new Dictionary<Guid, TenantRole>
        {
            [Tenant] = TenantRole.TenantAdministrator,
        };
        if (isAdmin)
        {
            controller.HttpContext.Items["IsAdmin"] = true;
        }

        return controller;
    }

    private static ReviewRunner CreateRunner(Guid tenantId) =>
        new(
            Guid.NewGuid(),
            tenantId,
            "runner",
            [],
            RunnerContractVersion.Current,
            "hash",
            "lookup",
            DateTimeOffset.UtcNow.AddDays(30),
            DateTimeOffset.UtcNow);

    private static RunnerFleetWorkload Workload(ReviewRunner runner, params RunnerExecutingJob[] executing) =>
        new(new Dictionary<Guid, RunnerWorkload> { [runner.Id] = new(executing.Length, 0, executing) }, 0, null);

    private static TenantDto CreateTenant(Guid id, string slug, string displayName) =>
        new(id, slug, displayName, true, true, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, InstallationDefaultCapturesReasoning: true);
}
