using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Controllers;

/// <summary>
///     Integration tests for <see cref="MeisterProPR.Api.Controllers.ClientsController" />
///     admin and crawl-configuration endpoints.
/// </summary>
public sealed class ClientsControllerTests(ClientsControllerTests.ClientsApiFactory factory)
    : IClassFixture<ClientsControllerTests.ClientsApiFactory>
{
    private const string ValidAdminKey = "admin-key-min-16-chars-ok";
    private const string ValidClientKey = "client-key-min-16-chars-ok";

    // ── DELETE /clients/{clientId}/ado-credentials (T029) ────────────────────

    [Fact]
    public async Task DeleteAdoCredentials_ExistingClient_Returns204()
    {
        var clientId = factory.ClientId;
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/clients/{clientId}/ado-credentials");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task DeleteAdoCredentials_UnknownClient_Returns404()
    {
        var unknownId = Guid.NewGuid();
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/clients/{unknownId}/ado-credentials");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteAdoCredentials_WithoutAdminKey_Returns401()
    {
        var clientId = factory.ClientId;
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/clients/{clientId}/ado-credentials");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── GET /clients/{clientId} — no secret in response (T032) ───────────────

    [Fact]
    public async Task GetClient_ResponseDoesNotContainSecret()
    {
        var clientId = factory.ClientId;
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/clients/{clientId}");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
        Assert.True(JsonDocument.Parse(body).RootElement.TryGetProperty("hasAdoCredentials", out _));
    }

    [Fact]
    public async Task GetClient_WithNoCredentials_HasAdoCredentialsFalse()
    {
        var clientId = factory.ClientId;
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/clients/{clientId}");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("hasAdoCredentials").GetBoolean());
    }

    // ── GET /clients — no secret in list response (T033) ─────────────────────

    [Fact]
    public async Task GetClients_ListResponseDoesNotContainSecret()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/clients");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
    }

    // ── GET /clients (T030) ───────────────────────────────────────────────────

    [Fact]
    public async Task GetClients_WithValidAdminKey_Returns200WithNoKeys()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/clients");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Headers.Add("X-Client-Key", ValidClientKey);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"key\"", body, StringComparison.OrdinalIgnoreCase);
    }

    // ── GET /clients/{clientId}/crawl-configurations (T031) ──────────────────

    [Fact]
    public async Task GetCrawlConfigs_WithOwnerKey_Returns200()
    {
        var clientId = factory.ClientId;

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/clients/{clientId}/crawl-configurations");
        request.Headers.Add("X-Client-Key", ValidClientKey);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetCrawlConfigs_WithWrongClient_Returns403()
    {
        var wrongClientId = Guid.NewGuid();

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/clients/{wrongClientId}/crawl-configurations");
        request.Headers.Add("X-Client-Key", ValidClientKey);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── PATCH /clients/{id} (T030) ────────────────────────────────────────────

    [Fact]
    public async Task PatchClient_ToggleIsActive_Returns200()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var record = new ClientRecord
        {
            Id = Guid.NewGuid(),
            Key = "patch-target-key-1234",
            DisplayName = "Patch Me",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Clients.Add(record);
        await db.SaveChangesAsync();

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/clients/{record.Id}");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Headers.Add("X-Client-Key", ValidClientKey);
        request.Content = JsonContent.Create(new { isActive = false });

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task PostClients_DuplicateKey_Returns409()
    {
        // Seed the DB with the key first
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        db.Clients.Add(
            new ClientRecord
            {
                Id = Guid.NewGuid(),
                Key = "duplicate-key-for-test",
                DisplayName = "Existing",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/clients");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Headers.Add("X-Client-Key", ValidClientKey);
        request.Content = JsonContent.Create(new { key = "duplicate-key-for-test", displayName = "Dup" });

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task PostClients_ShortKey_Returns400()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/clients");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Headers.Add("X-Client-Key", ValidClientKey);
        request.Content = JsonContent.Create(new { key = "short", displayName = "Bad" });

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostClients_WithoutAdminKey_Returns401()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/clients");
        request.Headers.Add("X-Client-Key", ValidClientKey);
        request.Content = JsonContent.Create(new { key = "some-key-here-1234", displayName = "Unauthorized" });

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── POST /clients (T030) ──────────────────────────────────────────────────

    [Fact]
    public async Task PostClients_WithValidAdminKey_Returns201()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/clients");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Headers.Add("X-Client-Key", ValidClientKey);
        request.Content = JsonContent.Create(new { key = "new-client-key-min-16", displayName = "Test Client" });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.TryGetProperty("id", out _));
        Assert.False(body.RootElement.TryGetProperty("key", out _), "Raw key must never be returned.");
        Assert.Equal("Test Client", body.RootElement.GetProperty("displayName").GetString());
        Assert.True(body.RootElement.GetProperty("isActive").GetBoolean());
    }

    // ── POST /clients/{clientId}/crawl-configurations (T031) ─────────────────

    [Fact]
    public async Task PostCrawlConfig_WithOwnerKey_Returns201()
    {
        // Seed a client that maps to ValidClientKey
        var clientId = factory.ClientId;

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/clients/{clientId}/crawl-configurations");
        request.Headers.Add("X-Client-Key", ValidClientKey);
        request.Content = JsonContent.Create(
            new
            {
                organizationUrl = "https://dev.azure.com/myorg",
                projectId = "MyProject",
                reviewerDisplayName = "Test Reviewer",
                crawlIntervalSeconds = 60,
            });

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task PostCrawlConfig_WithWrongClient_Returns403()
    {
        var wrongClientId = Guid.NewGuid(); // not the caller's client

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/clients/{wrongClientId}/crawl-configurations");
        request.Headers.Add("X-Client-Key", ValidClientKey);
        request.Content = JsonContent.Create(
            new
            {
                organizationUrl = "https://dev.azure.com/org",
                projectId = "proj",
                reviewerDisplayName = "Test Reviewer",
                crawlIntervalSeconds = 60,
            });

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PutAdoCredentials_MissingField_Returns400()
    {
        var clientId = factory.ClientId;
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/clients/{clientId}/ado-credentials");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        // Missing secret
        request.Content = JsonContent.Create(new { tenantId = "t", clientId = "c", secret = "" });

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PutAdoCredentials_UnknownClient_Returns404()
    {
        var unknownId = Guid.NewGuid();
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/clients/{unknownId}/ado-credentials");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Content = JsonContent.Create(new { tenantId = "t", clientId = "c", secret = "s" });

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PutAdoCredentials_WithoutAdminKey_Returns401()
    {
        var clientId = factory.ClientId;
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/clients/{clientId}/ado-credentials");
        request.Content = JsonContent.Create(new { tenantId = "t", clientId = "c", secret = "s" });

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── PUT /clients/{clientId}/ado-credentials (T018) ────────────────────────

    [Fact]
    public async Task PutAdoCredentials_WithValidFields_Returns204()
    {
        var clientId = factory.ClientId;
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/clients/{clientId}/ado-credentials");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Content = JsonContent.Create(
            new
            {
                tenantId = "tenant-id-abc",
                clientId = "client-id-abc",
                secret = "super-secret-value",
            });

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ── Factory ───────────────────────────────────────────────────────────────

    public sealed class ClientsApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = $"TestDb_Clients_{Guid.NewGuid()}";

        // Explicit root ensures all DbContext instances within this factory share the same in-memory store.
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
            // No DB_CONNECTION_STRING → InMemory mode for IJobRepository/IClientRegistry

            var dbName = this._dbName; // capture before lambda
            var dbRoot = this._dbRoot; // capture before lambda
            builder.ConfigureServices(services =>
            {
                // Replace external stubs
                services.AddSingleton(Substitute.For<IAdoTokenValidator>());
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedPullRequestFetcher>());

                // Provide an in-memory EF Core DB for ClientsController (which uses DbContext directly).
                // The explicit InMemoryDatabaseRoot guarantees all context instances share the same store.
                services.AddDbContext<MeisterProPRDbContext>(opts =>
                    opts.UseInMemoryDatabase(dbName, dbRoot));

                // Provide a stub ICrawlConfigurationRepository for crawl config endpoints
                var crawlRepo = Substitute.For<ICrawlConfigurationRepository>();
                crawlRepo.GetAllActiveAsync(Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([]));
                crawlRepo.GetByClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([]));
                crawlRepo.AddAsync(
                        Arg.Any<Guid>(),
                        Arg.Any<string>(),
                        Arg.Any<string>(),
                        Arg.Any<int>(),
                        Arg.Any<CancellationToken>())
                    .Returns(ci => Task.FromResult(
                        new CrawlConfigurationDto(
                            Guid.NewGuid(),
                            ci.ArgAt<Guid>(0),
                            ci.ArgAt<string>(1),
                            ci.ArgAt<string>(2),
                            null,
                            ci.ArgAt<int>(3),
                            true,
                            DateTimeOffset.UtcNow)));
                crawlRepo.SetActiveAsync(
                        Arg.Any<Guid>(),
                        Arg.Any<Guid>(),
                        Arg.Any<bool>(),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(true));
                services.AddSingleton(crawlRepo);

                // Provide a stub IClientRegistry that returns this factory's ClientId for ValidClientKey
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

                // Provide a stub IIdentityResolver that returns one identity for any display name.
                var identityResolver = Substitute.For<IIdentityResolver>();
                identityResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<ResolvedIdentity>>([new ResolvedIdentity(Guid.NewGuid(), "Test Reviewer")]));
                services.AddSingleton(identityResolver);

                // Provide an in-memory IClientAdoCredentialRepository
                var adoCredRepo = Substitute.For<IClientAdoCredentialRepository>();
                adoCredRepo.GetByClientIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<ClientAdoCredentials?>(null));
                adoCredRepo.UpsertAsync(Arg.Any<Guid>(), Arg.Any<ClientAdoCredentials>(), Arg.Any<CancellationToken>())
                    .Returns(Task.CompletedTask);
                adoCredRepo.ClearAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.CompletedTask);
                services.AddSingleton(adoCredRepo);
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = base.CreateHost(builder);

            // Seed the client record that maps to ValidClientKey so crawl-config endpoints work
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.Clients.Add(
                new ClientRecord
                {
                    Id = this.ClientId,
                    Key = ValidClientKey,
                    DisplayName = "Test Client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            db.SaveChanges();

            return host;
        }
    }
}
