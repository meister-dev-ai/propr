using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Controllers;

/// <summary>
///     Integration tests for <see cref="MeisterProPR.Api.Controllers.ClientAiConnectionsController" />.
///     T024 — covers create, activate (valid model, invalid model), deactivate, and delete.
/// </summary>
public sealed class ClientAiConnectionsControllerTests(ClientAiConnectionsControllerTests.AiConnectionsApiFactory factory)
    : IClassFixture<ClientAiConnectionsControllerTests.AiConnectionsApiFactory>
{
    private const string ValidAdminKey = "admin-key-min-16-chars-ok";
    private const string ValidClientKey = "client-key-min-16-chars-ok";

    private static readonly string[] DefaultModels = ["gpt-4o", "gpt-4o-mini"];

    // ─── helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> SeedConnectionAsync(string displayName = "Test Connection", string[]? models = null)
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/ai-connections");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
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
        request.Headers.Add("X-Admin-Key", ValidAdminKey);

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetAiConnections_WithOwnerKey_Returns200()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.ClientId}/ai-connections");
        request.Headers.Add("X-Client-Key", ValidClientKey);

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

    [Fact]
    public async Task GetAiConnections_WithWrongClientKey_Returns403()
    {
        var wrongClientId = Guid.NewGuid();
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{wrongClientId}/ai-connections");
        request.Headers.Add("X-Client-Key", ValidClientKey);

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ─── POST create ai-connection ───────────────────────────────────────────────

    [Fact]
    public async Task CreateAiConnection_WithValidPayload_Returns201WithDto()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/ai-connections");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
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
    }

    [Fact]
    public async Task CreateAiConnection_MissingDisplayName_Returns400()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/ai-connections");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
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
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
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
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Content = JsonContent.Create(new
        {
            displayName = "No Models",
            endpointUrl = "https://fake.openai.azure.com/",
            models = Array.Empty<string>(),
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
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
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
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
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
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
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
        activateReq.Headers.Add("X-Admin-Key", ValidAdminKey);
        activateReq.Content = JsonContent.Create(new { model = "gpt-4o" });
        await http.SendAsync(activateReq);

        // Now deactivate
        using var deactivateReq = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/ai-connections/{connectionId}/deactivate");
        deactivateReq.Headers.Add("X-Admin-Key", ValidAdminKey);

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
        request.Headers.Add("X-Admin-Key", ValidAdminKey);

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
        request.Headers.Add("X-Admin-Key", ValidAdminKey);

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
        deleteReq.Headers.Add("X-Admin-Key", ValidAdminKey);
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
        private readonly string _dbName = $"TestDb_AiConnections_{Guid.NewGuid()}";
        private readonly InMemoryDatabaseRoot _dbRoot = new();

        /// <summary>The UUID of the seeded client that maps to <c>ValidClientKey</c>.</summary>
        public Guid ClientId { get; } = Guid.NewGuid();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
            builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
            builder.UseSetting("MEISTER_CLIENT_KEYS", ValidClientKey);
            builder.UseSetting("MEISTER_ADMIN_KEY", ValidAdminKey);

            var dbName = this._dbName;
            var dbRoot = this._dbRoot;
            builder.ConfigureServices(services =>
            {
                // Stub external/infra services not needed for these tests
                services.AddSingleton(Substitute.For<IAdoTokenValidator>());
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedPrFetcher>());

                // InMemory EF Core DB (shared across all requests in this factory)
                services.AddDbContext<MeisterProPRDbContext>(opts =>
                    opts.UseInMemoryDatabase(dbName, dbRoot));

                // Use real repository implementations backed by InMemory DB
                services.AddScoped<IAiConnectionRepository, AiConnectionRepository>();

                // Stub IClientRegistry — maps ValidClientKey to this factory's ClientId
                var clientId = this.ClientId;
                var clientRegistry = Substitute.For<IClientRegistry>();
                clientRegistry.IsValidKey(ValidClientKey).Returns(true);
                clientRegistry.GetClientIdByKeyAsync(ValidClientKey, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<Guid?>(clientId));
                clientRegistry.GetClientIdByKeyAsync(
                        Arg.Is<string>(k => k != ValidClientKey),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<Guid?>(null));
                services.AddSingleton(clientRegistry);

                // Stub remaining dependencies that the app expects but aren't exercised here
                var userRepo = Substitute.For<IUserRepository>();
                userRepo.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<Domain.Entities.AppUser?>(null));
                services.AddSingleton(userRepo);

                var crawlRepo = Substitute.For<ICrawlConfigurationRepository>();
                crawlRepo.GetAllActiveAsync(Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<Application.DTOs.CrawlConfigurationDto>>([]));
                services.AddSingleton(crawlRepo);

                var adoCredRepo = Substitute.For<IClientAdoCredentialRepository>();
                adoCredRepo.GetByClientIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<ClientAdoCredentials?>(null));
                services.AddSingleton(adoCredRepo);
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
                    Key = ValidClientKey,
                    DisplayName = "AI Test Client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            db.SaveChanges();

            return host;
        }
    }
}
