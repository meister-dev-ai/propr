// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.IdentityAndAccess.Authentication.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess.Authentication.Persistence;
using System.Text.Json;
using MeisterDev.ProPR.Api.Features.IdentityAndAccess.Authentication;
using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MeisterDev.ProPR.Api.Tests.Features.Clients;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Api.Tests.Fixtures;

namespace MeisterDev.ProPR.Api.Tests.IdentityAndAccess;

public sealed class TenantMachineCredentialTests
{
    [Fact]
    public void OperationAllowlist_MatchesRegisteredMvcActions()
    {
        using var factory = new TenantAdministrationApiFactory();
        var descriptors = factory.Services.GetRequiredService<Microsoft.AspNetCore.Mvc.Infrastructure.IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items.OfType<ControllerActionDescriptor>().ToArray();
        TenantMachineOperationPolicy.ValidateRegisteredActions(descriptors);
        Assert.NotEmpty(TenantMachineOperationPolicy.AllowedActions);
        Assert.Throws<InvalidOperationException>(() => TenantMachineOperationPolicy.ValidateRegisteredActions([]));
    }

    [Fact]
    public void OperationAllowlist_CannotBeModifiedThroughItsPublicCollection()
    {
        var collection = Assert.IsAssignableFrom<ICollection<(string Controller, string Action)>>(TenantMachineOperationPolicy.AllowedActions);
        Assert.Throws<NotSupportedException>(() => collection.Clear());
        Assert.True(TenantMachineOperationPolicy.IsAllowed("Clients", "GetClient"));
        Assert.False(TenantMachineOperationPolicy.IsAllowed("Tenants", "CreateTenant"));
    }

    [Fact]
    public void CredentialService_PublicBoundaryUsesApplicationContracts()
    {
        var boundaryTypes = typeof(TenantMachineCredentialService).GetConstructors()
            .SelectMany(constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType))
            .Concat(
                typeof(TenantMachineCredentialService).GetMethods(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
                    .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType)));
        Assert.All(boundaryTypes, type => Assert.DoesNotContain("Infrastructure", type.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Credential_IsLimitedToItsTenantAndCurrentClientOwnership()
    {
        await using var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var client = Guid.NewGuid();
        db.Tenants.AddRange(
            new TenantRecord { Id = tenantA, Slug = "a", DisplayName = "A" },
            new TenantRecord { Id = tenantB, Slug = "b", DisplayName = "B" });
        db.Clients.Add(new ClientRecord { Id = client, TenantId = tenantA, DisplayName = "Client" });
        await db.SaveChangesAsync();

        var service = new TenantMachineCredentialService(new EfTenantMachineCredentialStore(db));
        var issued = await service.IssueAsync(tenantA, "Hosted backend", null, Guid.NewGuid(), default);
        Assert.NotNull(issued);
        Assert.Equal(tenantA, (await service.AuthenticateAsync(issued!.Token, default))?.TenantId);
        Assert.True(await service.OwnsClientAsync(tenantA, client, default));
        Assert.False(await service.OwnsClientAsync(tenantB, client, default));

        db.Clients.Single().TenantId = tenantB;
        await db.SaveChangesAsync();
        Assert.False(await service.OwnsClientAsync(tenantA, client, default));

        await service.RevokeAsync(tenantA, issued.Id, Guid.NewGuid(), default);
        Assert.Null(await service.AuthenticateAsync(issued.Token, default));
        Assert.DoesNotContain(issued.Token, db.TenantMachineCredentials.Single().TokenHash);
    }

    [Theory]
    [InlineData("Clients", "GetClient", true)]
    [InlineData("ClientAiConnections", "VerifyUpdateAiConnection", true)]
    [InlineData("ClientAiConnections", "SelectAiPurposes", true)]
    [InlineData("ClientLogicalModels", "ListEffective", true)]
    [InlineData("ClientLogicalModels", "ListPurposeRoles", true)]
    [InlineData("ClientLogicalModels", "ListOverrides", false)]
    [InlineData("ClientLogicalModels", "CreateOverride", false)]
    [InlineData("ClientLogicalModels", "UpdateOverride", false)]
    [InlineData("ClientLogicalModels", "RenameOverride", false)]
    [InlineData("ClientLogicalModels", "DeleteOverride", false)]
    [InlineData("ClientLogicalModels", "SetPurposeRole", false)]
    [InlineData("ClientLogicalModels", "RemovePurposeRole", false)]
    [InlineData("Reviews", "ListReviews", true)]
    [InlineData("Reviews", "GetDashboard", true)]
    [InlineData("Reviews", "GetHistory", true)]
    [InlineData("ReviewJobs", "GetClientReview", true)]
    [InlineData("ReviewJobs", "GetReview", true)]
    [InlineData("ReviewJobs", "SubmitReviewByCoordinates", true)]
    [InlineData("CompletedReviewUsage", "GetCompletedUsage", true)]
    [InlineData("ClientReviewTargets", "GetTargets", true)]
    [InlineData("ClientReviewTargets", "CreateTarget", true)]
    [InlineData("ClientReviewTargets", "UpdateTargetPolicy", true)]
    [InlineData("ClientReviewTargets", "GetRepositories", true)]
    [InlineData("ClientReviewTargets", "GetOpenReviews", true)]
    [InlineData("ClientReviewTargets", "GetOverview", true)]
    [InlineData("AdminCrawlConfigs", "CreateCrawlConfiguration", false)]
    [InlineData("Clients", "PatchClient", false)]
    [InlineData("Jobs", "GetJobProtocol", false)]
    [InlineData("ClientProviderConnections", "CreateConnection", false)]
    [InlineData("Tenants", "CreateTenant", false)]
    [InlineData("AdminUsers", "ListUsers", false)]
    public void OperationAllowlist_IsExplicit(string controller, string action, bool expected)
    {
        Assert.Equal(expected, TenantMachineOperationPolicy.IsAllowed(controller, action));
    }

    [Theory]
    [InlineData("Clients", "GetClient")]
    [InlineData("ClientReviewTargets", "GetOverview")]
    [InlineData("ClientAiConnections", "VerifyUpdateAiConnection")]
    [InlineData("ClientAiConnections", "SelectAiPurposes")]
    [InlineData("ClientLogicalModels", "ListEffective")]
    [InlineData("ClientLogicalModels", "ListPurposeRoles")]
    public async Task EndpointAuthorization_RechecksOwnershipAfterClientMoves(string controller, string action)
    {
        await using var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        db.Clients.Add(new ClientRecord { Id = clientId, TenantId = tenantA, DisplayName = "Client" });
        await db.SaveChangesAsync();
        var services = new ServiceCollection().AddSingleton(db)
            .AddSingleton(new TenantMachineCredentialService(new EfTenantMachineCredentialStore(db))).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.SetEndpoint(
            new Endpoint(
                _ => Task.CompletedTask,
                new EndpointMetadataCollection(
                    new ControllerActionDescriptor
                    {
                        ControllerName = controller, ActionName = action,
                    }), "client"));
        context.Request.RouteValues["clientId"] = clientId.ToString();
        context.Items[TenantMachineOperationPolicy.TenantItemKey] = tenantB;
        Assert.False(await TenantMachineOperationPolicy.AuthorizeAsync(context));
        context.Items[TenantMachineOperationPolicy.TenantItemKey] = tenantA;

        Assert.True(await TenantMachineOperationPolicy.AuthorizeAsync(context));
        var roles = Assert.IsType<Dictionary<Guid, MeisterDev.ProPR.Domain.Enums.ClientRole>>(context.Items["ClientRoles"]);
        Assert.Equal(clientId, Assert.Single(roles).Key);
        Assert.Equal(MeisterDev.ProPR.Domain.Enums.ClientRole.ClientAdministrator, roles[clientId]);
        Assert.Null(AuthHelpers.RequireClientRole(context, clientId, MeisterDev.ProPR.Domain.Enums.ClientRole.ClientUser));

        context.Items[TenantMachineOperationPolicy.AuthorizedItemKey] = false;
        db.Clients.Single().TenantId = tenantB;
        await db.SaveChangesAsync();
        Assert.False(await TenantMachineOperationPolicy.AuthorizeAsync(context));
    }

    [Fact]
    public async Task ReviewResultAuthorization_ResolvesJobOwnerFromDatabase()
    {
        await using var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        db.Clients.Add(new ClientRecord { Id = clientId, TenantId = tenantB, DisplayName = "Client" });
        db.ReviewJobs.Add(new ReviewJob(jobId, clientId, "https://example.test", "project", "repo", 1, 1));
        await db.SaveChangesAsync();
        var services = new ServiceCollection().AddSingleton(db)
            .AddSingleton(new TenantMachineCredentialService(new EfTenantMachineCredentialStore(db))).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.SetEndpoint(
            new Endpoint(
                _ => Task.CompletedTask,
                new EndpointMetadataCollection(
                    new ControllerActionDescriptor
                    {
                        ControllerName = "ReviewJobs", ActionName = "GetReview",
                    }), "review"));
        context.Request.RouteValues["jobId"] = jobId.ToString();
        context.Items[TenantMachineOperationPolicy.TenantItemKey] = tenantA;

        Assert.False(await TenantMachineOperationPolicy.AuthorizeAsync(context));
        context.Items[TenantMachineOperationPolicy.TenantItemKey] = tenantB;
        Assert.True(await TenantMachineOperationPolicy.AuthorizeAsync(context));
    }

    [Fact]
    public async Task LifecycleEndpoints_RequirePlatformAdminAndDoNotListSecrets()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new TenantRecord { Id = tenantId, Slug = "tenant", DisplayName = "Tenant" });
        await db.SaveChangesAsync();
        var service = new TenantMachineCredentialService(new EfTenantMachineCredentialStore(db));
        var context = new DefaultHttpContext();
        var controller = new TenantMachineCredentialsController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };

        var rejected = Assert.IsType<ObjectResult>(
            await controller.Issue(
                tenantId,
                new IssueTenantMachineCredentialRequest("hosted", null), default));
        Assert.Equal(StatusCodes.Status401Unauthorized, rejected.StatusCode);

        context.Items["IsAdmin"] = true;
        context.Items["UserId"] = Guid.NewGuid().ToString();
        var created = Assert.IsType<ObjectResult>(
            await controller.Issue(
                tenantId,
                new IssueTenantMachineCredentialRequest("hosted", null), default));
        var issued = Assert.IsType<IssuedTenantMachineCredential>(created.Value);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        var listed = Assert.IsType<OkObjectResult>(await controller.List(tenantId, default));
        var item = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<TenantMachineCredentialSummary>>(listed.Value));
        Assert.Equal(issued.Id, item.Id);
        var listedBody = JsonSerializer.Serialize(listed.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("token", listedBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(issued.Token, listedBody, StringComparison.Ordinal);

        Assert.IsType<NoContentResult>(await controller.Revoke(tenantId, issued.Id, default));
        Assert.Null(await service.AuthenticateAsync(issued.Token, default));
        Assert.Equal(2, db.TenantAuditEntries.Count());
    }

    [Fact]
    public async Task HttpMachineCredential_ConfinesClientReadsAndRefusesAdminEndpoint()
    {
        using var factory = new TenantAdministrationApiFactory();
        using var client = factory.CreateClient();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        var siblingClient = Guid.NewGuid();
        var siblingJob = Guid.NewGuid();
        var jobB = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.Tenants.AddRange(
                new TenantRecord { Id = tenantA, Slug = "machine-a", DisplayName = "A" },
                new TenantRecord { Id = tenantB, Slug = "machine-b", DisplayName = "B" });
            db.Clients.AddRange(
                new ClientRecord { Id = clientA, TenantId = tenantA, DisplayName = "A client" },
                new ClientRecord { Id = clientB, TenantId = tenantB, DisplayName = "B client" },
                new ClientRecord { Id = siblingClient, TenantId = tenantA, DisplayName = "Sibling" });
            db.ReviewJobs.Add(new ReviewJob(jobB, clientB, "https://example.test", "project", "repo", 1, 1));
            db.ReviewJobs.Add(new ReviewJob(siblingJob, siblingClient, "https://example.test", "project", "repo", 2, 1));
            await db.SaveChangesAsync();
        }

        var admin = new HttpRequestMessage(HttpMethod.Post, $"/admin/tenants/{tenantA}/machine-credentials")
        {
            Content = JsonContent.Create(new { label = "Hosted A" }),
        };
        admin.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(Guid.NewGuid(), MeisterDev.ProPR.Domain.Enums.AppUserRole.Admin));
        var issuedResponse = await client.SendAsync(admin);
        Assert.Equal(HttpStatusCode.Created, issuedResponse.StatusCode);
        using var body = JsonDocument.Parse(await issuedResponse.Content.ReadAsStringAsync());
        var token = body.RootElement.GetProperty("token").GetString();
        var credentialId = body.RootElement.GetProperty("id").GetGuid();
        Assert.NotNull(token);

        using var list = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/tenants/{tenantA}/machine-credentials");
        list.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(Guid.NewGuid(), MeisterDev.ProPR.Domain.Enums.AppUserRole.Admin));
        var listResponse = await client.SendAsync(list);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listJson = await listResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(token, listJson, StringComparison.Ordinal);
        using var listedCredentials = JsonDocument.Parse(listJson);
        var listed = Assert.Single(listedCredentials.RootElement.EnumerateArray());
        Assert.Equal(credentialId, listed.GetProperty("id").GetGuid());
        Assert.False(listed.TryGetProperty("token", out _));
        Assert.False(listed.TryGetProperty("tokenHash", out _));
        Assert.False(listed.TryGetProperty("tokenLookupHash", out _));

        using var own = new HttpRequestMessage(HttpMethod.Get, $"/clients/{clientA}");
        own.Headers.Add("X-Tenant-Machine-Token", token);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(own)).StatusCode);

        using var ownDashboard = new HttpRequestMessage(HttpMethod.Get, $"/clients/{clientA}/reviewing/dashboard");
        ownDashboard.Headers.Add("X-Tenant-Machine-Token", token);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(ownDashboard)).StatusCode);

        using var ownHistory = new HttpRequestMessage(HttpMethod.Get, $"/clients/{clientA}/reviewing/history");
        ownHistory.Headers.Add("X-Tenant-Machine-Token", token);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(ownHistory)).StatusCode);

        using var siblingResult = new HttpRequestMessage(HttpMethod.Get, $"/clients/{clientA}/reviewing/jobs/{siblingJob}/status");
        siblingResult.Headers.Add("X-Tenant-Machine-Token", token);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(siblingResult)).StatusCode);
        using var ownedResult = new HttpRequestMessage(HttpMethod.Get, $"/clients/{siblingClient}/reviewing/jobs/{siblingJob}/status");
        ownedResult.Headers.Add("X-Tenant-Machine-Token", token);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(ownedResult)).StatusCode);

        using var other = new HttpRequestMessage(HttpMethod.Get, $"/clients/{clientB}");
        other.Headers.Add("X-Tenant-Machine-Token", token);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(other)).StatusCode);

        foreach (var path in new[]
                 {
                     $"/clients/{clientB}/reviewing/dashboard",
                     $"/clients/{clientB}/reviewing/history",
                     $"/clients/{clientB}/reviewing/jobs/{jobB}/status",
                     $"/clients/{clientB}/reviews",
                     $"/reviews/{jobB}",
                     $"/clients/{clientB}/provider-connections",
                     $"/clients/{clientB}/provider-connections/{Guid.NewGuid()}/scopes",
                     $"/clients/{clientB}/ai-connections",
                 })
        {
            using var crossTenant = new HttpRequestMessage(HttpMethod.Get, path);
            crossTenant.Headers.Add("X-Tenant-Machine-Token", token);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(crossTenant)).StatusCode);
        }

        using var adminOnly = new HttpRequestMessage(HttpMethod.Get, "/admin/tenants");
        adminOnly.Headers.Add("X-Tenant-Machine-Token", token);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(adminOnly)).StatusCode);

        using var revoke = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/admin/tenants/{tenantA}/machine-credentials/{credentialId}");
        revoke.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(Guid.NewGuid(), MeisterDev.ProPR.Domain.Enums.AppUserRole.Admin));
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(revoke)).StatusCode);
        using var revoked = new HttpRequestMessage(HttpMethod.Get, $"/clients/{clientA}");
        revoked.Headers.Add("X-Tenant-Machine-Token", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(revoked)).StatusCode);

        using var mixed = new HttpRequestMessage(HttpMethod.Get, "/admin/tenants");
        mixed.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(Guid.NewGuid(), MeisterDev.ProPR.Domain.Enums.AppUserRole.Admin));
        mixed.Headers.Add("X-Tenant-Machine-Token", "mprm_invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(mixed)).StatusCode);
    }

    [Fact]
    public async Task LifecycleEndpoints_RejectAdminTokenWithoutParseableActorId()
    {
        using var factory = new TenantAdministrationApiFactory();
        using var client = factory.CreateClient();
        var tenantId = Guid.NewGuid();
        var token = factory.GenerateToken("invalid-subject", MeisterDev.ProPR.Domain.Enums.AppUserRole.Admin);

        using var issue = new HttpRequestMessage(
            HttpMethod.Post,
            $"/admin/tenants/{tenantId}/machine-credentials")
        {
            Content = JsonContent.Create(new { label = "Hosted" }),
        };
        issue.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(issue)).StatusCode);

        using var revoke = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/admin/tenants/{tenantId}/machine-credentials/{Guid.NewGuid()}");
        revoke.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(revoke)).StatusCode);
    }

    [Fact]
    public async Task ExpiredAndMalformedTokensAreRejected()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new TenantRecord { Id = tenantId, Slug = "tenant", DisplayName = "Tenant" });
        await db.SaveChangesAsync();
        var service = new TenantMachineCredentialService(new EfTenantMachineCredentialStore(db));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.IssueAsync(tenantId, "Expired", DateTimeOffset.UtcNow.AddMinutes(-1), Guid.NewGuid(), default));
        Assert.Empty(db.TenantMachineCredentials);
        Assert.Null(await service.AuthenticateAsync("mprm_invalid", default));
    }

    [Fact]
    public async Task Issue_NormalizesPositiveOffsetExpiryToUtc()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new TenantRecord { Id = tenantId, Slug = "tenant", DisplayName = "Tenant" });
        await db.SaveChangesAsync();
        var service = new TenantMachineCredentialService(new EfTenantMachineCredentialStore(db));
        var expiry = DateTimeOffset.UtcNow.AddDays(1).ToOffset(TimeSpan.FromHours(2));

        var issued = await service.IssueAsync(tenantId, "Offset", expiry, Guid.NewGuid(), default);

        Assert.Equal(TimeSpan.Zero, issued!.ExpiresAt!.Value.Offset);
        Assert.Equal(expiry.UtcDateTime, db.TenantMachineCredentials.Single().ExpiresAt!.Value.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, db.TenantMachineCredentials.Single().ExpiresAt!.Value.Offset);
    }

    [Fact]
    public async Task MachineCredential_AllowsOwnScmWriteAndRejectsForeignScmWrite()
    {
        using var factory = new ClientProviderConnectionsControllerTests.ProviderConnectionsApiFactory();
        factory.EnableMachineTenantTest();
        await factory.ResetProviderStateAsync();
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TenantMachineCredentialService>();
        var issued = await service.IssueAsync(factory.MachineTenantId, "Hosted", null, Guid.NewGuid(), default);
        Assert.NotNull(issued);

        static object ConnectionBody() => new
        {
            providerFamily = "github",
            hostBaseUrl = "https://github.example.com",
            authenticationKind = "personalAccessToken",
            displayName = "Hosted GitHub",
            secret = "ghp_test_secret_value",
            isActive = true,
        };

        using var own = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/provider-connections")
        {
            Content = JsonContent.Create(ConnectionBody()),
        };
        own.Headers.Add("X-Tenant-Machine-Token", issued!.Token);
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(own)).StatusCode);

        using var foreign = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.OtherClientId}/provider-connections")
        {
            Content = JsonContent.Create(ConnectionBody()),
        };
        foreign.Headers.Add("X-Tenant-Machine-Token", issued.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(foreign)).StatusCode);

        var repository = scope.ServiceProvider.GetRequiredService<IClientScmConnectionRepository>();
        Assert.Single(await repository.GetByClientIdAsync(factory.ClientId, default));
        Assert.Empty(await repository.GetByClientIdAsync(factory.OtherClientId, default));
    }

    [Fact]
    public async Task MachineHeader_IsThrottledBeforeCredentialAuthentication()
    {
        using var factory = new TenantAdministrationApiFactory();
        factory.SetMachineThrottle(1, 16, TimeSpan.FromMinutes(1));
        using var client = factory.CreateClient();
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        string token;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.Tenants.Add(new TenantRecord { Id = tenantId, Slug = "throttle", DisplayName = "Throttle" });
            db.Clients.Add(new ClientRecord { Id = clientId, TenantId = tenantId, DisplayName = "Client" });
            await db.SaveChangesAsync();
            token = (await scope.ServiceProvider.GetRequiredService<TenantMachineCredentialService>()
                .IssueAsync(tenantId, "Hosted", null, Guid.NewGuid(), default))!.Token;
        }

        using var first = new HttpRequestMessage(HttpMethod.Get, $"/clients/{clientId}");
        first.Headers.Add("X-Tenant-Machine-Token", token);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(first)).StatusCode);
        using var second = new HttpRequestMessage(HttpMethod.Get, $"/clients/{clientId}");
        second.Headers.Add("X-Tenant-Machine-Token", "mprm_invalid");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(second)).StatusCode);
    }

    [Fact]
    public async Task MachineCredentialThrottle_IsIndependentPerCredential()
    {
        using var factory = new TenantAdministrationApiFactory();
        factory.SetMachineThrottle(100, 1, TimeSpan.FromMinutes(1));
        using var client = factory.CreateClient();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        string tokenA;
        string tokenB;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.Tenants.AddRange(
                new TenantRecord { Id = tenantA, Slug = "throttle-a", DisplayName = "A" },
                new TenantRecord { Id = tenantB, Slug = "throttle-b", DisplayName = "B" });
            db.Clients.AddRange(
                new ClientRecord { Id = clientA, TenantId = tenantA, DisplayName = "A" },
                new ClientRecord { Id = clientB, TenantId = tenantB, DisplayName = "B" });
            await db.SaveChangesAsync();
            var service = scope.ServiceProvider.GetRequiredService<TenantMachineCredentialService>();
            tokenA = (await service.IssueAsync(tenantA, "A", null, Guid.NewGuid(), default))!.Token;
            tokenB = (await service.IssueAsync(tenantB, "B", null, Guid.NewGuid(), default))!.Token;
        }

        async Task<HttpStatusCode> GetStatusAsync(Guid clientId, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/clients/{clientId}");
            request.Headers.Add("X-Tenant-Machine-Token", token);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(clientA, tokenA));
        Assert.Equal(HttpStatusCode.TooManyRequests, await GetStatusAsync(clientA, tokenA));
        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(clientB, tokenB));
    }

    [Fact]
    public async Task ConcurrentRevoke_ProducesOneAuditEntryAcrossContexts()
    {
        var databaseName = $"machine-concurrent-{Guid.NewGuid()}";
        var root = new InMemoryDatabaseRoot();
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase(databaseName, root).Options;
        var tenantId = Guid.NewGuid();
        Guid credentialId;
        await using (var db = new MeisterProPRDbContext(options))
        {
            db.Tenants.Add(new TenantRecord { Id = tenantId, Slug = "concurrent", DisplayName = "Concurrent" });
            await db.SaveChangesAsync();
            credentialId = (await new TenantMachineCredentialService(new EfTenantMachineCredentialStore(db))
                .IssueAsync(tenantId, "Hosted", null, Guid.NewGuid(), default))!.Id;
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<bool> RevokeAsync()
        {
            await using var db = new MeisterProPRDbContext(options);
            await gate.Task;
            return await new TenantMachineCredentialService(new EfTenantMachineCredentialStore(db))
                .RevokeAsync(tenantId, credentialId, Guid.NewGuid(), default);
        }

        var revocations = Enumerable.Range(0, 8).Select(_ => Task.Run(RevokeAsync)).ToArray();
        gate.SetResult();
        Assert.All(await Task.WhenAll(revocations), Assert.True);

        await using var verificationDb = new MeisterProPRDbContext(options);
        Assert.Equal(1, await verificationDb.TenantAuditEntries.CountAsync(x => x.TenantId == tenantId && x.EventType == "machine_credential_revoked"));
    }

    private static MeisterProPRDbContext CreateDb()
    {
        return new MeisterProPRDbContext(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseInMemoryDatabase($"machine-{Guid.NewGuid()}").Options);
    }
}

[Collection("PostgresApiIntegration")]
public sealed class TenantMachineCredentialConcurrencyTests(PostgresContainerFixture fixture)
{
    [Xunit.SkippableFact]
    public async Task ConcurrentRevoke_RecordsOneAuditEntry()
    {
        fixture.SkipIfUnavailable();
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector()).Options;
        var tenantId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        Guid credentialId;
        await using (var db = new MeisterProPRDbContext(options))
        {
            db.Tenants.Add(new TenantRecord { Id = tenantId, Slug = $"machine-{tenantId:N}", DisplayName = "Machine" });
            db.AppUsers.Add(
                new AppUserRecord
                {
                    Id = actorId, Username = $"machine-actor-{actorId:N}", CreatedAt = DateTimeOffset.UtcNow,
                });
            await db.SaveChangesAsync();
            var offsetExpiry = DateTimeOffset.UtcNow.AddDays(1).ToOffset(TimeSpan.FromHours(2));
            credentialId = (await new TenantMachineCredentialService(new EfTenantMachineCredentialStore(db))
                .IssueAsync(tenantId, "Concurrent", offsetExpiry, actorId, default))!.Id;
            db.ChangeTracker.Clear();
            var storedExpiry = await db.TenantMachineCredentials.Where(x => x.Id == credentialId)
                .Select(x => x.ExpiresAt).SingleAsync();
            Assert.Equal(TimeSpan.Zero, storedExpiry!.Value.Offset);
        }

        try
        {
            await using var firstDb = new MeisterProPRDbContext(options);
            await using var secondDb = new MeisterProPRDbContext(options);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<bool> RevokeAsync(MeisterProPRDbContext db)
            {
                await gate.Task;
                return await new TenantMachineCredentialService(new EfTenantMachineCredentialStore(db))
                    .RevokeAsync(tenantId, credentialId, actorId, default);
            }

            var first = Task.Run(() => RevokeAsync(firstDb));
            var second = Task.Run(() => RevokeAsync(secondDb));
            gate.SetResult();
            Assert.All(await Task.WhenAll(first, second), Assert.True);

            await using var verificationDb = new MeisterProPRDbContext(options);
            Assert.Equal(1, await verificationDb.TenantAuditEntries.CountAsync(x => x.TenantId == tenantId && x.EventType == "machine_credential_revoked"));
        }
        finally
        {
            await using var cleanupDb = new MeisterProPRDbContext(options);
            await cleanupDb.Tenants.Where(x => x.Id == tenantId).ExecuteDeleteAsync();
            await cleanupDb.AppUsers.Where(x => x.Id == actorId).ExecuteDeleteAsync();
        }
    }
}
