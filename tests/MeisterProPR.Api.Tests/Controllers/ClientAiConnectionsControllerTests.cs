// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MeisterProPR.Application.DTOs;
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
///     Integration tests for <see cref="MeisterProPR.Api.Controllers.ClientAiConnectionsController" />.
///     T024 — covers create, activate (valid model, invalid model), deactivate, and delete.
/// </summary>
public sealed class ClientAiConnectionsControllerTests(ClientAiConnectionsControllerTests.AiConnectionsApiFactory factory)
    : IClassFixture<ClientAiConnectionsControllerTests.AiConnectionsApiFactory>
{
    private static readonly string[] DefaultModels = ["gpt-4o", "gpt-4o-mini"];

    // ─── helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> SeedConnectionAsync(string displayName = "Test Connection", string[]? models = null)
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/ai-connections");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Content = JsonContent.Create(new
        {
            displayName,
            endpointUrl = "https://fake.openai.azure.com/",
            models = models ?? DefaultModels,
        });

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetGuid();
    }

    // ─── GET ai-connections ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetAiConnections_WithAdminKey_Returns200()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.ClientId}/ai-connections");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetAiConnections_WithoutCredentials_Returns401()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.ClientId}/ai-connections");

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ─── POST create ai-connection ───────────────────────────────────────────────

    [Fact]
    public async Task CreateAiConnection_WithValidPayload_Returns201WithDto()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/ai-connections");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Content = JsonContent.Create(new
        {
            displayName = "My Connection",
            endpointUrl = "https://fake.openai.azure.com/",
            models = new[] { "gpt-4o" },
            apiKey = "sk-test-secret",
        });

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.TryGetProperty("id", out _));
        Assert.Equal("My Connection", body.GetProperty("displayName").GetString());
        Assert.False(body.GetProperty("isActive").GetBoolean());
        // API key must never appear in the response
        Assert.False(body.TryGetProperty("apiKey", out _), "apiKey must not be returned.");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var record = await db.AiConnections.FirstAsync(a => a.DisplayName == "My Connection");
        Assert.NotEqual("sk-test-secret", record.ApiKey);
    }

    [Fact]
    public async Task CreateAiConnection_MissingDisplayName_Returns400()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/ai-connections");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Content = JsonContent.Create(new
        {
            endpointUrl = "https://fake.openai.azure.com/",
            models = new[] { "gpt-4o" },
        });

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateAiConnection_InvalidEndpointUrl_Returns400()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/ai-connections");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Content = JsonContent.Create(new
        {
            displayName = "Bad Endpoint",
            endpointUrl = "not-a-url",
            models = new[] { "gpt-4o" },
        });

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateAiConnection_EmptyModelsList_Returns400()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/ai-connections");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Content = JsonContent.Create(new
        {
            displayName = "No Models",
            endpointUrl = "https://fake.openai.azure.com/",
            models = Array.Empty<string>(),
        });

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ─── PATCH update ai-connection ──────────────────────────────────────────────

    [Fact]
    public async Task UpdateAiConnection_WithEndpointAndModels_UpdatesConnection()
    {
        var connectionId = await this.SeedConnectionAsync("Editable Connection", ["gpt-4o", "gpt-4o-mini"]);

        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/clients/{factory.ClientId}/ai-connections/{connectionId}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Content = JsonContent.Create(new
        {
            displayName = "Renamed Connection",
            endpointUrl = "https://updated-resource.openai.azure.com/",
            models = new[] { "gpt-4.1" },
            apiKey = "sk-updated-secret",
        });

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Renamed Connection", body.GetProperty("displayName").GetString());
        Assert.Equal("https://updated-resource.openai.azure.com/", body.GetProperty("endpointUrl").GetString());
        Assert.Equal("gpt-4.1", body.GetProperty("models")[0].GetString());
        Assert.False(body.TryGetProperty("apiKey", out _));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var record = await db.AiConnections.FirstAsync(a => a.Id == connectionId);
        Assert.Equal("https://updated-resource.openai.azure.com/", record.EndpointUrl);
        Assert.Equal(["gpt-4.1"], record.Models);
        Assert.NotEqual("sk-updated-secret", record.ApiKey);
    }

    [Fact]
    public async Task UpdateAiConnection_InvalidEndpointUrl_Returns400()
    {
        var connectionId = await this.SeedConnectionAsync();

        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/clients/{factory.ClientId}/ai-connections/{connectionId}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Content = JsonContent.Create(new
        {
            endpointUrl = "not-a-valid-url",
        });

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdateAiConnection_RemovingActiveModel_Returns400()
    {
        var connectionId = await this.SeedConnectionAsync(models: ["gpt-4o", "gpt-4o-mini"]);

        var http = factory.CreateClient();
        using (var activateRequest = new HttpRequestMessage(
                   HttpMethod.Post,
                   $"/clients/{factory.ClientId}/ai-connections/{connectionId}/activate"))
        {
            activateRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
            activateRequest.Content = JsonContent.Create(new { model = "gpt-4o" });
            var activateResponse = await http.SendAsync(activateRequest);
            Assert.Equal(HttpStatusCode.OK, activateResponse.StatusCode);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/clients/{factory.ClientId}/ai-connections/{connectionId}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Content = JsonContent.Create(new
        {
            models = new[] { "gpt-4o-mini" },
        });

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ─── POST activate ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ActivateAiConnection_WithValidModel_Returns200AndIsActiveTrue()
    {
        var connectionId = await this.SeedConnectionAsync();

        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/ai-connections/{connectionId}/activate");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Content = JsonContent.Create(new { model = "gpt-4o" });

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.GetProperty("isActive").GetBoolean());
        Assert.Equal("gpt-4o", body.GetProperty("activeModel").GetString());
    }

    [Fact]
    public async Task ActivateAiConnection_WithModelNotInList_Returns400()
    {
        var connectionId = await this.SeedConnectionAsync(models: ["gpt-4o"]);

        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/ai-connections/{connectionId}/activate");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Content = JsonContent.Create(new { model = "o1-preview" }); // not in list

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ActivateAiConnection_UnknownConnection_Returns404()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/ai-connections/{Guid.NewGuid()}/activate");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Content = JsonContent.Create(new { model = "gpt-4o" });

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ─── POST deactivate ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DeactivateAiConnection_WhenActive_Returns200AndIsActiveFalse()
    {
        var connectionId = await this.SeedConnectionAsync();

        // Activate first
        var http = factory.CreateClient();
        using var activateReq = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/ai-connections/{connectionId}/activate");
        activateReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        activateReq.Content = JsonContent.Create(new { model = "gpt-4o" });
        await http.SendAsync(activateReq);

        // Now deactivate
        using var deactivateReq = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/ai-connections/{connectionId}/deactivate");
        deactivateReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await http.SendAsync(deactivateReq);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.False(body.GetProperty("isActive").GetBoolean());
    }

    // ─── DELETE ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAiConnection_ExistingConnection_Returns204()
    {
        var connectionId = await this.SeedConnectionAsync("Delete Me");

        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/clients/{factory.ClientId}/ai-connections/{connectionId}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task DeleteAiConnection_UnknownConnection_Returns404()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/clients/{factory.ClientId}/ai-connections/{Guid.NewGuid()}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteAiConnection_WithoutCredentials_Returns401()
    {
        var connectionId = await this.SeedConnectionAsync("No Auth Delete");

        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/clients/{factory.ClientId}/ai-connections/{connectionId}");

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ─── FR-017: deleting a connection must not corrupt existing job snapshots ───

    [Fact]
    public async Task DeleteAiConnection_DoesNotCorruptExistingJobAiConnectionSnapshot()
    {
        // Arrange: seed a connection, activate it, then record the ID (simulates what a job would snapshot).
        var connectionId = await this.SeedConnectionAsync("FR-017 Connection");

        // Seed a ReviewJob that references this connection directly in the DB
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            var job = new ReviewJob(
                Guid.NewGuid(),
                factory.ClientId,
                "https://dev.azure.com/org",
                "proj",
                "repo",
                1,
                1);
            job.SetAiConfig(connectionId, "gpt-4o"); // FR-017: set the AI connection snapshot
            db.ReviewJobs.Add(job);
            await db.SaveChangesAsync();
        }

        // Act: delete the AI connection
        var http = factory.CreateClient();
        using var deleteReq = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/clients/{factory.ClientId}/ai-connections/{connectionId}");
        deleteReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        var deleteResp = await http.SendAsync(deleteReq);
        Assert.Equal(HttpStatusCode.NoContent, deleteResp.StatusCode);

        // Assert: job record still has its AiConnectionId snapshot (nullable FK — job is not deleted)
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            var job = await db.ReviewJobs.FirstOrDefaultAsync(j => j.AiConnectionId == connectionId);
            // Job still exists (connection deletion does not cascade to jobs in our design)
            // The AiConnectionId on the job remains the snapshot value (soft reference).
            // In production with a real DB, the FK is nullable and SET NULL on delete.
            // In InMemory, deleting the AiConnectionRecord does not cascade, so the job record persists.
            Assert.NotNull(job);
            Assert.Equal(connectionId, job.AiConnectionId);
        }
    }

    // ─── WebApplicationFactory ────────────────────────────────────────────────────

    public sealed class AiConnectionsApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-ai-connections-jwt-32chars!!";
        private readonly string _dbName = $"TestDb_AiConnections_{Guid.NewGuid()}";
        private readonly InMemoryDatabaseRoot _dbRoot = new();

        /// <summary>The UUID of the seeded client.</summary>
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
                // Stub external/infra services not needed for these tests
                services.AddSingleton<IJwtTokenService, JwtTokenService>();
                services.AddSingleton(Substitute.For<IAdoTokenValidator>());
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedPrFetcher>());
                services.AddSingleton(Substitute.For<IPrStatusFetcher>());
                services.AddSingleton(Substitute.For<IThreadMemoryService>());

                // InMemory EF Core DB (shared across all requests in this factory)
                services.AddDbContext<MeisterProPRDbContext>(opts =>
                    opts.UseInMemoryDatabase(dbName, dbRoot));

                // Use real repository implementations backed by InMemory DB
                services.AddScoped<IAiConnectionRepository, AiConnectionRepository>();

                // Stub remaining dependencies that the app expects but aren't exercised here
                var userRepo = Substitute.For<IUserRepository>();
                userRepo.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<Domain.Entities.AppUser?>(null));
                userRepo.GetUserClientRolesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>()));
                services.AddSingleton(userRepo);

                var crawlRepo = Substitute.For<ICrawlConfigurationRepository>();
                crawlRepo.GetAllActiveAsync(Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<Application.DTOs.CrawlConfigurationDto>>([]));
                services.AddSingleton(crawlRepo);

                var adoCredRepo = Substitute.For<IClientAdoCredentialRepository>();
                adoCredRepo.GetByClientIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<ClientAdoCredentials?>(null));
                services.AddSingleton(adoCredRepo);
                services.AddSingleton(Substitute.For<IJobRepository>());
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = base.CreateHost(builder);

            // Seed the client record so ClientAiConnectionsController can validate ownership
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.Clients.Add(
                new ClientRecord
                {
                    Id = this.ClientId,
                    DisplayName = "AI Test Client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            db.SaveChanges();

            return host;
        }
    }
}
