// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Auth;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Repositories;
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
///     Integration tests for <see cref="MeisterProPR.Api.Controllers.ClientTokenUsageController" />.
/// </summary>
public sealed class ClientTokenUsageControllerTests(ClientTokenUsageControllerTests.TokenUsageApiFactory factory)
    : IClassFixture<ClientTokenUsageControllerTests.TokenUsageApiFactory>
{
    // T055: GET /admin/clients/{id}/token-usage returns empty samples for a client with no jobs.
    [Fact]
    public async Task GetByClientAndDateRange_ReturnsEmptySamples_ForClientWithNoJobs()
    {
        var http = factory.CreateClient();
        var from = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7)).ToString("yyyy-MM-dd");
        var to = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/clients/{factory.ClientId}/token-usage?from={from}&to={to}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(factory.ClientId.ToString(), body.GetProperty("clientId").GetString());
        var samples = body.GetProperty("samples");
        Assert.Equal(JsonValueKind.Array, samples.ValueKind);
        Assert.Equal(0, samples.GetArrayLength());
        Assert.Equal(0, body.GetProperty("totalInputTokens").GetInt64());
        Assert.Equal(0, body.GetProperty("totalOutputTokens").GetInt64());
    }

    // T056: GET /admin/clients/{id}/token-usage returns samples grouped by model and date.
    [Fact]
    public async Task GetByClientAndDateRange_ReturnsSamplesGroupedByModelAndDate()
    {
        // Seed data directly into the DB
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var yesterday = today.AddDays(-1);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.ClientTokenUsageSamples.AddRange(
                new ClientTokenUsageSample
                {
                    Id = Guid.NewGuid(),
                    ClientId = factory.ClientId,
                    ModelId = "gpt-4o",
                    Date = today,
                    InputTokens = 1000,
                    OutputTokens = 500,
                },
                new ClientTokenUsageSample
                {
                    Id = Guid.NewGuid(),
                    ClientId = factory.ClientId,
                    ModelId = "gpt-5-mini",
                    Date = yesterday,
                    InputTokens = 200,
                    OutputTokens = 100,
                });
            await db.SaveChangesAsync();
        }

        var http = factory.CreateClient();
        var from = yesterday.ToString("yyyy-MM-dd");
        var to = today.ToString("yyyy-MM-dd");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/clients/{factory.ClientId}/token-usage?from={from}&to={to}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        // Verify totals
        Assert.Equal(1200, body.GetProperty("totalInputTokens").GetInt64());
        Assert.Equal(600, body.GetProperty("totalOutputTokens").GetInt64());

        // Verify samples shape
        var samples = body.GetProperty("samples");
        Assert.Equal(2, samples.GetArrayLength());

        // Verify at least one sample has the expected shape (modelId, date, inputTokens, outputTokens)
        var firstSample = samples[0];
        Assert.True(firstSample.TryGetProperty("modelId", out _));
        Assert.True(firstSample.TryGetProperty("date", out _));
        Assert.True(firstSample.TryGetProperty("inputTokens", out _));
        Assert.True(firstSample.TryGetProperty("outputTokens", out _));
    }

    [Fact]
    public async Task GetByClientAndDateRange_WithoutCredentials_Returns401()
    {
        var http = factory.CreateClient();
        var from = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7)).ToString("yyyy-MM-dd");
        var to = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/clients/{factory.ClientId}/token-usage?from={from}&to={to}");

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public sealed class TokenUsageApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-token-usage-jwt-32-chars!!x";

        private readonly string _dbName = $"TestDb_TokenUsage_{Guid.NewGuid()}";
        private readonly InMemoryDatabaseRoot _dbRoot = new();

        public Guid ClientId { get; } = Guid.NewGuid();

        public string GenerateAdminToken()
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var descriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity([
                    new Claim("sub", Guid.NewGuid().ToString()),
                    new Claim("global_role", "Admin"),
                ]),
                Expires = DateTime.UtcNow.AddHours(1),
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
                Issuer = "meisterpropr",
                Audience = "meisterpropr",
            };
            return handler.WriteToken(handler.CreateToken(descriptor));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
            builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
            builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

            var dbName = this._dbName;
            var dbRoot = this._dbRoot;

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IJwtTokenService, JwtTokenService>();

                services.AddSingleton(Substitute.For<IAdoTokenValidator>());
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedPrFetcher>());
                services.AddSingleton(Substitute.For<IJobRepository>());

                services.AddDbContext<MeisterProPRDbContext>(opts =>
                    opts.UseInMemoryDatabase(dbName, dbRoot));

                var userRepo = Substitute.For<IUserRepository>();
                userRepo.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AppUser?>(null));
                userRepo.GetUserClientRolesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>()));
                services.AddSingleton(userRepo);

                // Register the real ClientTokenUsageRepository backed by InMemory EF
                services.AddScoped<IClientTokenUsageRepository, ClientTokenUsageRepository>();

                var adoCredRepo = Substitute.For<IClientAdoCredentialRepository>();
                adoCredRepo.GetByClientIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<MeisterProPR.Application.DTOs.ClientAdoCredentials?>(null));
                services.AddSingleton(adoCredRepo);
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
                    DisplayName = "Token Usage Test Client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            db.SaveChanges();

            return host;
        }
    }
}
