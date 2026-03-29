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
///     Integration tests for <see cref="MeisterProPR.Api.Controllers.ClientsController" />
///     admin and crawl-configuration endpoints.
/// </summary>
public sealed class ClientsControllerTests(ClientsControllerTests.ClientsApiFactory factory)
    : IClassFixture<ClientsControllerTests.ClientsApiFactory>
{
    private const string ValidAdminKey = "admin-key-min-16-chars-ok";
    private const string ValidClientKey = "client-key-min-16-chars-ok";


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

    // T036 — PATCH /clients/{id} customSystemMessage (admin)

    [Fact]
    public async Task PatchClient_CustomSystemMessage_PersistedAndReturned()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var record = new ClientRecord
        {
            Id = Guid.NewGuid(),
            Key = "patch-csm-key-12345",
            DisplayName = "CSM Test",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Clients.Add(record);
        await db.SaveChangesAsync();

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/clients/{record.Id}");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Content = JsonContent.Create(new { customSystemMessage = "Focus on security." });

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Focus on security.", body.RootElement.GetProperty("customSystemMessage").GetString());
    }

    [Fact]
    public async Task PatchClient_CustomSystemMessageTooLong_Returns400()
    {
        var clientId = factory.ClientId;
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/clients/{clientId}");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Content = JsonContent.Create(new { customSystemMessage = new string('x', 20_001) });

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PatchClient_NullCustomSystemMessage_LeavesExistingValueUnchanged()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var record = new ClientRecord
        {
            Id = Guid.NewGuid(),
            Key = "patch-csm-null-key-1",
            DisplayName = "CSM Null Test",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            CustomSystemMessage = "Original message.",
        };
        db.Clients.Add(record);
        await db.SaveChangesAsync();

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/clients/{record.Id}");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Content = JsonContent.Create(new { isActive = true }); // no customSystemMessage field

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Original message.", body.RootElement.GetProperty("customSystemMessage").GetString());
    }

    [Fact]
    public async Task PatchClient_EmptyCustomSystemMessage_ClearsValue()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var record = new ClientRecord
        {
            Id = Guid.NewGuid(),
            Key = "patch-csm-clear-key1",
            DisplayName = "CSM Clear Test",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            CustomSystemMessage = "Will be cleared.",
        };
        db.Clients.Add(record);
        await db.SaveChangesAsync();

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/clients/{record.Id}");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Content = JsonContent.Create(new { customSystemMessage = "" });

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(
            body.RootElement.GetProperty("customSystemMessage").ValueKind == JsonValueKind.Null,
            "customSystemMessage should be null after clearing with empty string");
    }

    // T037 — PATCH /client/me customSystemMessage (client self-service)

    [Fact]
    public async Task PatchClientMe_CustomSystemMessage_PersistedAndReturned()
    {
        var clientId = factory.ClientId;
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/client/me");
        request.Headers.Add("X-Client-Key", ValidClientKey);
        request.Content = JsonContent.Create(new { customSystemMessage = "Self-service guidance." });

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Self-service guidance.", body.RootElement.GetProperty("customSystemMessage").GetString());
    }

    [Fact]
    public async Task PatchClientMe_CustomSystemMessageTooLong_Returns400()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/client/me");
        request.Headers.Add("X-Client-Key", ValidClientKey);
        request.Content = JsonContent.Create(new { customSystemMessage = new string('y', 20_001) });

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PatchClientMe_WithoutClientKey_Returns401()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/client/me");
        request.Content = JsonContent.Create(new { customSystemMessage = "Test" });

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }


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
                services.AddSingleton(Substitute.For<IAssignedPrFetcher>());

                // Provide an in-memory EF Core DB backing IClientAdminService.
                // The explicit InMemoryDatabaseRoot guarantees all context instances share the same store.
                services.AddDbContext<MeisterProPRDbContext>(opts =>
                    opts.UseInMemoryDatabase(dbName, dbRoot));
                services.AddScoped<IClientAdminService, ClientAdminService>();

                // IUserRepository stub (GetClients now injects it for non-admin JWT users)
                var userRepo = Substitute.For<IUserRepository>();
                userRepo.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<Domain.Entities.AppUser?>(null));
                services.AddSingleton(userRepo);

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
                            DateTimeOffset.UtcNow,
                            [])));
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

/// <summary>
///     T036 — integration tests covering JWT-based access to <c>GET /clients</c>.
///     These tests are initially RED (failing) until T038 makes <c>GetClients</c> role-aware.
/// </summary>
public sealed class ClientsJwtGetTests(ClientsJwtGetTests.ClientsJwtApiFactory factory)
    : IClassFixture<ClientsJwtGetTests.ClientsJwtApiFactory>
{
    // (a) Admin JWT → returns all clients
    [Fact]
    public async Task GetClients_AdminJwt_Returns200WithAllClients()
    {
        var http = factory.CreateClient();
        var token = factory.GenerateToken(factory.AdminUserId, AppUserRole.Admin);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/clients");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        // Admin should see all 3 seeded clients
        Assert.Equal(3, items.GetArrayLength());
    }

    // (b) User JWT with 2 assigned clients — FAILING until T038
    [Fact]
    public async Task GetClients_UserJwtWithTwoAssignments_Returns200WithScopedClients()
    {
        var http = factory.CreateClient();
        var token = factory.GenerateToken(factory.TestUserId, AppUserRole.User);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/clients");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        // User should see only their 2 assigned clients, not all 3
        Assert.Equal(2, items.GetArrayLength());
    }

    // (c) User JWT with zero assignments — FAILING until T038
    [Fact]
    public async Task GetClients_UserJwtWithNoAssignments_Returns200WithEmptyList()
    {
        var http = factory.CreateClient();
        var token = factory.GenerateToken(factory.UnassignedUserId, AppUserRole.User);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/clients");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        Assert.Equal(0, items.GetArrayLength());
    }

    // (d) No credentials → 401
    [Fact]
    public async Task GetClients_NoCredentials_Returns401()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/clients");

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public sealed class ClientsJwtApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test_jwt_secret_that_is_32_chars!";
        private const string ValidAdminKey = "admin-key-min-16-chars-ok";
        private const string ValidClientKey = "jwt-test-client-key-min-16-chars";

        private readonly string _dbName = $"TestDb_ClientsJwt_{Guid.NewGuid()}";
        private readonly InMemoryDatabaseRoot _dbRoot = new();

        /// <summary>ID of the admin user.</summary>
        public Guid AdminUserId { get; } = Guid.NewGuid();

        /// <summary>ID of the normal user who has 2 client assignments.</summary>
        public Guid TestUserId { get; } = Guid.NewGuid();

        /// <summary>ID of a normal user with no client assignments.</summary>
        public Guid UnassignedUserId { get; } = Guid.NewGuid();

        /// <summary>IDs of the 3 seeded clients.</summary>
        public Guid ClientAId { get; } = Guid.NewGuid();
        public Guid ClientBId { get; } = Guid.NewGuid();
        public Guid ClientCId { get; } = Guid.NewGuid();

        public string GenerateToken(Guid userId, AppUserRole role)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var token = new JwtSecurityToken(
                issuer: "meisterpropr",
                audience: "meisterpropr",
                claims:
                [
                    new Claim("sub", userId.ToString()),
                    new Claim("global_role", role.ToString()),
                ],
                expires: DateTime.UtcNow.AddHours(1),
                signingCredentials: creds);
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
            builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
            builder.UseSetting("MEISTER_ADMIN_KEY", ValidAdminKey);
            builder.UseSetting("MEISTER_CLIENT_KEYS", ValidClientKey);
            builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

            var dbName = this._dbName;
            var dbRoot = this._dbRoot;

            builder.ConfigureServices(services =>
            {
                services.AddSingleton(Substitute.For<IAdoTokenValidator>());
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedPrFetcher>());

                services.AddDbContext<MeisterProPRDbContext>(opts =>
                    opts.UseInMemoryDatabase(dbName, dbRoot));
                services.AddScoped<IClientAdminService, ClientAdminService>();

                // IJwtTokenService must be explicit for in-memory (non-DB) mode
                services.AddSingleton<IJwtTokenService, JwtTokenService>();

                // IUserRepository stub: two users with known assignments
                var testUserId = this.TestUserId;
                var unassignedUserId = this.UnassignedUserId;
                var clientAId = this.ClientAId;
                var clientBId = this.ClientBId;

                var testUser = new AppUser
                {
                    Id = testUserId,
                    Username = "testuser",
                    GlobalRole = AppUserRole.User,
                    IsActive = true,
                };
                testUser.ClientAssignments.Add(new UserClientRole { UserId = testUserId, ClientId = clientAId });
                testUser.ClientAssignments.Add(new UserClientRole { UserId = testUserId, ClientId = clientBId });

                var unassignedUser = new AppUser
                {
                    Id = unassignedUserId,
                    Username = "unassigned",
                    GlobalRole = AppUserRole.User,
                    IsActive = true,
                };

                var userRepo = Substitute.For<IUserRepository>();
                userRepo.GetByIdWithAssignmentsAsync(testUserId, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AppUser?>(testUser));
                userRepo.GetByIdWithAssignmentsAsync(unassignedUserId, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AppUser?>(unassignedUser));
                userRepo.GetByIdWithAssignmentsAsync(
                        Arg.Is<Guid>(id => id != testUserId && id != unassignedUserId),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AppUser?>(null));
                services.AddSingleton(userRepo);

                // Stub ICrawlConfigurationRepository (not used by GetClients but needed for DI resolution)
                var crawlRepo = Substitute.For<ICrawlConfigurationRepository>();
                crawlRepo.GetAllActiveAsync(Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([]));
                crawlRepo.GetByClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([]));
                services.AddSingleton(crawlRepo);

                // Stub IClientRegistry
                var clientRegistry = Substitute.For<IClientRegistry>();
                clientRegistry.IsValidKey(ValidClientKey).Returns(true);
                clientRegistry.GetClientIdByKeyAsync(ValidClientKey, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<Guid?>(this.ClientAId));
                services.AddSingleton(clientRegistry);

                var adoCredRepo = Substitute.For<IClientAdoCredentialRepository>();
                adoCredRepo.GetByClientIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<ClientAdoCredentials?>(null));
                services.AddSingleton(adoCredRepo);
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = base.CreateHost(builder);

            // Seed 3 clients
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.Clients.AddRange(
                new ClientRecord { Id = this.ClientAId, Key = "client-a-key-min-16", DisplayName = "Client A", IsActive = true, CreatedAt = DateTimeOffset.UtcNow },
                new ClientRecord { Id = this.ClientBId, Key = "client-b-key-min-16", DisplayName = "Client B", IsActive = true, CreatedAt = DateTimeOffset.UtcNow },
                new ClientRecord { Id = this.ClientCId, Key = "client-c-key-min-16", DisplayName = "Client C", IsActive = true, CreatedAt = DateTimeOffset.UtcNow });
            db.SaveChanges();

            return host;
        }
    }
}
