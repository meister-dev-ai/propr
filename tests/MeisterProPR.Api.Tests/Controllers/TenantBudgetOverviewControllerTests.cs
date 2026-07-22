// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Budgeting;
using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Auth;
using MeisterProPR.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Controllers;

/// <summary>
///     Integration tests for <see cref="MeisterProPR.Api.Controllers.TenantBudgetOverviewController" />:
///     authentication, the Budgeting license gate, and delegation to the overview service.
/// </summary>
public sealed class TenantBudgetOverviewControllerTests(TenantBudgetOverviewControllerTests.TenantBudgetApiFactory factory)
    : IClassFixture<TenantBudgetOverviewControllerTests.TenantBudgetApiFactory>
{
    [Fact]
    public async Task GetOverview_WithoutCredentials_Returns401()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/admin/tenants/{factory.TenantId}/budget/overview");

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetOverview_WhenLicensed_ReturnsPerClientSpend()
    {
        factory.BudgetingAvailable = true;
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/admin/tenants/{factory.TenantId}/budget/overview");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(factory.TenantId.ToString(), body.GetProperty("tenantId").GetString());
        var clients = body.GetProperty("clients");
        Assert.Equal(2, clients.GetArrayLength());
        Assert.Equal("Globex", clients[0].GetProperty("displayName").GetString());
        Assert.Equal(70m, clients[0].GetProperty("spentToDateUsd").GetDecimal());
    }

    [Fact]
    public async Task GetOverview_WhenNotLicensed_ReturnsPremiumUnavailable()
    {
        factory.BudgetingAvailable = false;
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/admin/tenants/{factory.TenantId}/budget/overview");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task GetSpend_WhenLicensed_ReturnsAggregateSpendAndTrend()
    {
        factory.BudgetingAvailable = true;
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/admin/tenants/{factory.TenantId}/budget/spend?months=6");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(factory.TenantId.ToString(), body.GetProperty("tenantId").GetString());
        Assert.Equal(100m, body.GetProperty("spentToDateUsd").GetDecimal());
        Assert.Equal(200m, body.GetProperty("monthlyHardCapUsd").GetDecimal());
        Assert.Equal(2, body.GetProperty("months").GetArrayLength());
    }

    public sealed class TenantBudgetApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-tenant-budget-overview-jwt!";

        private readonly string _dbName = $"TestDb_TenantBudgetOverview_{Guid.NewGuid()}";
        private readonly InMemoryDatabaseRoot _dbRoot = new();

        public Guid TenantId { get; } = Guid.NewGuid();

        /// <summary>Toggles whether the substituted licensing service reports the Budgeting capability as available.</summary>
        public bool BudgetingAvailable { get; set; } = true;

        public string GenerateAdminToken()
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var descriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(
                [
                    new Claim("sub", Guid.NewGuid().ToString()),
                    new Claim("global_role", AppUserRole.Admin.ToString()),
                ]),
                Expires = DateTime.UtcNow.AddHours(1),
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
                Issuer = "meisterpropr",
                Audience = "meisterpropr",
            };
            return handler.WriteToken(handler.CreateToken(descriptor));
        }

        private TenantBudgetOverviewDto SampleOverview() =>
            new(
                this.TenantId,
                new DateOnly(2026, 7, 1),
                new DateOnly(2026, 7, 31),
                new DateOnly(2026, 7, 15),
                [
                    new TenantBudgetOverviewClientDto(Guid.NewGuid(), "Globex", 70m, null, null, 90m),
                    new TenantBudgetOverviewClientDto(Guid.NewGuid(), "Acme", 30m, 80m, 100m, 60m),
                ]);

        private TenantSpendDto SampleSpend() =>
            new(
                this.TenantId,
                new DateOnly(2026, 7, 1),
                new DateOnly(2026, 7, 31),
                new DateOnly(2026, 7, 15),
                100m,
                80m,
                200m,
                130m,
                [
                    new TenantSpendMonthDto(2026, 6, new DateOnly(2026, 6, 1), 90m),
                    new TenantSpendMonthDto(2026, 7, new DateOnly(2026, 7, 1), 100m),
                ]);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
            builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
            builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
            builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

            var dbName = this._dbName;
            var dbRoot = this._dbRoot;

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IJwtTokenService, JwtTokenService>();

                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedReviewDiscoveryService>());
                services.AddSingleton(Substitute.For<IJobRepository>());
                services.AddSingleton(Substitute.For<IUserRepository>());

                services.AddDbContext<MeisterProPRDbContext>(opts =>
                    opts.UseInMemoryDatabase(dbName, dbRoot));

                var overview = Substitute.For<ITenantBudgetOverviewService>();
                overview.GetOverviewAsync(this.TenantId, Arg.Any<CancellationToken>())
                    .Returns(_ => Task.FromResult(this.SampleOverview()));
                services.AddScoped(_ => overview);

                var spend = Substitute.For<ITenantBudgetSpendService>();
                spend.GetSpendAsync(this.TenantId, Arg.Any<int>(), Arg.Any<CancellationToken>())
                    .Returns(_ => Task.FromResult(this.SampleSpend()));
                services.AddScoped(_ => spend);

                var licensing = Substitute.For<ILicensingCapabilityService>();
                licensing.GetCapabilityAsync(PremiumCapabilityKey.Budgeting, Arg.Any<CancellationToken>())
                    .Returns(_ => Task.FromResult(
                        new CapabilitySnapshot(
                            PremiumCapabilityKey.Budgeting,
                            "Budgeting",
                            RequiresCommercial: true,
                            DefaultWhenCommercial: true,
                            OverrideState: PremiumCapabilityOverrideState.Default,
                            IsAvailable: this.BudgetingAvailable,
                            Message: this.BudgetingAvailable ? null : "Budgeting requires a commercial license.")));
                services.AddScoped(_ => licensing);
            });
        }
    }
}
