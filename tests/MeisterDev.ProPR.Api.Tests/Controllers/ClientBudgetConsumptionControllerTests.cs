// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Auth;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Controllers;

/// <summary>
///     Integration tests for <see cref="MeisterDev.ProPR.Api.Controllers.ClientBudgetConsumptionController" />:
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

    [Fact]
    public async Task GetConsumption_WithPeriod_PassesTheParsedMonthToTheService()
    {
        factory.BudgetingAvailable = true;
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/admin/clients/{factory.ClientId}/budget/consumption?period=2026-06");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await factory.Consumption.Received().GetConsumptionAsync(factory.ClientId, 2026, 6, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetConsumption_WithMalformedPeriod_Returns400()
    {
        factory.BudgetingAvailable = true;
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/admin/clients/{factory.ClientId}/budget/consumption?period=not-a-month");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetHistory_WhenLicensed_ReturnsPerMonthSpend()
    {
        factory.BudgetingAvailable = true;
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/admin/clients/{factory.ClientId}/budget/history?months=6");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(100m, body.GetProperty("monthlyHardCapUsd").GetDecimal());
        var months = body.GetProperty("months");
        Assert.Equal(JsonValueKind.Array, months.ValueKind);
        Assert.Equal(2, months.GetArrayLength());
        Assert.Equal(42m, months[1].GetProperty("spentUsd").GetDecimal());
    }

    [Fact]
    public async Task GetHistory_WhenNotLicensed_ReturnsPremiumUnavailable()
    {
        factory.BudgetingAvailable = false;
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/admin/clients/{factory.ClientId}/budget/history");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ResetSpend_WithoutCredentials_Returns401()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/admin/clients/{factory.ClientId}/budget/reset");

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ResetSpend_WhenApplied_ReturnsTheRecordedResetWithItsBeforeAndAfterCaps()
    {
        factory.BudgetingAvailable = true;
        factory.ResetOutcome = BudgetSpendResetOutcome.Applied;
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/admin/clients/{factory.ClientId}/budget/reset");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(100m, body.GetProperty("effectiveHardCapBeforeUsd").GetDecimal());
        Assert.Equal(200m, body.GetProperty("effectiveHardCapAfterUsd").GetDecimal());
        Assert.Equal(100m, body.GetProperty("topUpHardCapUsd").GetDecimal());
    }

    [Fact]
    public async Task ResetSpend_PassesTheAuthenticatedAdministratorAsTheActor()
    {
        factory.BudgetingAvailable = true;
        factory.ResetOutcome = BudgetSpendResetOutcome.Applied;
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/admin/clients/{factory.ClientId}/budget/reset");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        await http.SendAsync(request);

        await factory.Reset.Received().ResetAsync(
            factory.ClientId,
            Arg.Is<Guid?>(actor => actor != null && actor != Guid.Empty),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResetSpend_WhenNoCapIsConfigured_Returns400()
    {
        factory.BudgetingAvailable = true;
        factory.ResetOutcome = BudgetSpendResetOutcome.NoMonthlyCapConfigured;
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/admin/clients/{factory.ClientId}/budget/reset");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ResetSpend_WhenTheClientIsUnknown_Returns404()
    {
        factory.BudgetingAvailable = true;
        factory.ResetOutcome = BudgetSpendResetOutcome.ClientNotFound;
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/admin/clients/{factory.ClientId}/budget/reset");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ResetSpend_WhenNotLicensed_ReturnsPremiumUnavailableAndGrantsNothing()
    {
        factory.BudgetingAvailable = false;
        factory.Reset.ClearReceivedCalls();
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/admin/clients/{factory.ClientId}/budget/reset");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("premium_feature_unavailable", body.GetProperty("error").GetString());
        // The gate must run before the grant, not merely shape the response after it.
        await factory.Reset.DidNotReceive().ResetAsync(
            Arg.Any<Guid>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResetSpend_WithoutCredentials_GrantsNothing()
    {
        factory.BudgetingAvailable = true;
        factory.Reset.ClearReceivedCalls();
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/admin/clients/{factory.ClientId}/budget/reset");

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await factory.Reset.DidNotReceive().ResetAsync(
            Arg.Any<Guid>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
    }

    public sealed class BudgetApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-budget-consumption-jwt-32ch";

        private readonly string _dbName = $"TestDb_BudgetConsumption_{Guid.NewGuid()}";
        private readonly InMemoryDatabaseRoot _dbRoot = new();

        public Guid ClientId { get; } = Guid.NewGuid();

        /// <summary>The substituted consumption service, exposed so tests can assert on the arguments it received.</summary>
        public IClientBudgetConsumptionService Consumption { get; } = Substitute.For<IClientBudgetConsumptionService>();

        /// <summary>The substituted reset service, exposed so tests can assert on the arguments it received.</summary>
        public IClientBudgetResetService Reset { get; } = Substitute.For<IClientBudgetResetService>();

        /// <summary>Steers what the substituted reset service reports, so the controller's mapping can be tested.</summary>
        public BudgetSpendResetOutcome ResetOutcome { get; set; } = BudgetSpendResetOutcome.Applied;

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
                [new BudgetDailySpendDto(new DateOnly(2026, 7, 15), 42m)],
                []);

        private ClientBudgetHistoryDto SampleHistory() =>
            new(
                this.ClientId,
                80m,
                100m,
                [
                    new BudgetMonthSpendDto(2026, 6, new DateOnly(2026, 6, 1), 55m, false, 80m, 100m),
                    new BudgetMonthSpendDto(2026, 7, new DateOnly(2026, 7, 1), 42m, false, 160m, 200m, 1),
                ]);

        private BudgetSpendResetResult SampleResetResult() =>
            this.ResetOutcome is BudgetSpendResetOutcome.Applied
                ? new BudgetSpendResetResult(
                    BudgetSpendResetOutcome.Applied,
                    new BudgetSpendReset
                    {
                        Id = Guid.NewGuid(),
                        ClientId = this.ClientId,
                        PeriodStart = new DateOnly(2026, 7, 1),
                        TopUpSoftCapUsd = 80m,
                        TopUpHardCapUsd = 100m,
                        EffectiveSoftCapBeforeUsd = 80m,
                        EffectiveSoftCapAfterUsd = 160m,
                        EffectiveHardCapBeforeUsd = 100m,
                        EffectiveHardCapAfterUsd = 200m,
                        ActorUserId = Guid.NewGuid(),
                        PerformedAt = new DateTime(2026, 7, 15, 9, 14, 0, DateTimeKind.Utc),
                    })
                : new BudgetSpendResetResult(this.ResetOutcome, null);

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
                this.Consumption.GetConsumptionAsync(this.ClientId, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
                    .Returns(_ => Task.FromResult(this.SampleConsumption()));
                this.Consumption.GetHistoryAsync(this.ClientId, Arg.Any<int>(), Arg.Any<CancellationToken>())
                    .Returns(_ => Task.FromResult(this.SampleHistory()));
                services.AddScoped(_ => this.Consumption);

                // Substitute the reset service the same way, so the endpoint's status mapping is what gets tested.
                this.Reset.ResetAsync(this.ClientId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
                    .Returns(_ => Task.FromResult(this.SampleResetResult()));
                services.AddScoped(_ => this.Reset);

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
