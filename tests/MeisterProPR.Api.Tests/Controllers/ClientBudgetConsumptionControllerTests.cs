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
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Auth;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
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
///     Integration tests for <see cref="MeisterProPR.Api.Controllers.ClientBudgetConsumptionController" />:
///     authentication, the Budgeting license gate, and delegation to the consumption service.
/// </summary>
public sealed class ClientBudgetConsumptionControllerTests(ClientBudgetConsumptionControllerTests.BudgetApiFactory factory)
    : IClassFixture<ClientBudgetConsumptionControllerTests.BudgetApiFactory>
{
    [Fact]
    public async Task GetConsumption_WithoutCredentials_Returns401()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/admin/clients/{factory.ClientId}/budget/consumption");

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetConsumption_WhenLicensed_ReturnsConsumption()
    {
        factory.BudgetingAvailable = true;
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/admin/clients/{factory.ClientId}/budget/consumption");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(factory.ClientId.ToString(), body.GetProperty("clientId").GetString());
        Assert.Equal(42m, body.GetProperty("spentToDateUsd").GetDecimal());
        Assert.Equal(100m, body.GetProperty("monthlyHardCapUsd").GetDecimal());
        Assert.Equal(88m, body.GetProperty("projectedPeriodSpendUsd").GetDecimal());
    }

    [Fact]
    public async Task GetConsumption_WhenNotLicensed_ReturnsPremiumUnavailable()
    {
        factory.BudgetingAvailable = false;
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/admin/clients/{factory.ClientId}/budget/consumption");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("premium_feature_unavailable", body.GetProperty("error").GetString());
        Assert.Equal(PremiumCapabilityKey.Budgeting, body.GetProperty("feature").GetString());
    }

    public sealed class BudgetApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-budget-consumption-jwt-32ch";

        private readonly string _dbName = $"TestDb_BudgetConsumption_{Guid.NewGuid()}";
        private readonly InMemoryDatabaseRoot _dbRoot = new();

        public Guid ClientId { get; } = Guid.NewGuid();

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

        private ClientBudgetConsumptionDto SampleConsumption() =>
            new(
                this.ClientId,
                new DateOnly(2026, 7, 1),
                new DateOnly(2026, 7, 31),
                new DateOnly(2026, 8, 1),
                new DateOnly(2026, 7, 15),
                42m,
                false,
                80m,
                100m,
                88m,
                [new BudgetDailySpendDto(new DateOnly(2026, 7, 15), 42m)]);

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

                // Substitute the consumption service so the controller test isolates auth + the license gate.
                var consumption = Substitute.For<IClientBudgetConsumptionService>();
                consumption.GetConsumptionAsync(this.ClientId, Arg.Any<CancellationToken>())
                    .Returns(_ => Task.FromResult(this.SampleConsumption()));
                services.AddScoped(_ => consumption);

                // Substitute licensing so availability is controllable per test via BudgetingAvailable.
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

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = base.CreateHost(builder);

            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.Clients.Add(
                new ClientRecord
                {
                    Id = this.ClientId,
                    DisplayName = "Budget Consumption Test Client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            db.SaveChanges();

            return host;
        }
    }
}
