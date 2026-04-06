// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.DTOs.AzureDevOps;
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


    [Fact]
    public async Task DeleteAdoCredentials_ExistingClient_Returns204()
    {
        var clientId = factory.ClientId;
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/clients/{clientId}/ado-credentials");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task DeleteAdoCredentials_UnknownClient_Returns404()
    {
        var unknownId = Guid.NewGuid();
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/clients/{unknownId}/ado-credentials");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

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
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

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
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

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
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

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
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"key\"", body, StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public async Task PatchClient_ToggleIsActive_Returns200()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var record = new ClientRecord
        {
            Id = Guid.NewGuid(),
            DisplayName = "Patch Me",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Clients.Add(record);
        await db.SaveChangesAsync();

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/clients/{record.Id}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Content = JsonContent.Create(new { isActive = false });

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task PostClients_DuplicateDisplayName_Returns201()
    {
        // Seed the DB with a client first
        var existingClientId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        db.Clients.Add(
            new ClientRecord
            {
                Id = existingClientId,
                DisplayName = "Existing",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/clients");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Content = JsonContent.Create(new { displayName = "Existing" });

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.NotEqual(existingClientId.ToString(), body.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task PostClients_WithoutAdminKey_Returns401()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/clients");
        request.Content = JsonContent.Create(new { displayName = "Unauthorized" });

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }


    [Fact]
    public async Task PostClients_WithValidAdminKey_Returns201()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/clients");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Content = JsonContent.Create(new { displayName = "Test Client" });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.TryGetProperty("id", out _));
        Assert.False(body.RootElement.TryGetProperty("key", out _), "Raw key must never be returned.");
        Assert.Equal("Test Client", body.RootElement.GetProperty("displayName").GetString());
        Assert.True(body.RootElement.GetProperty("isActive").GetBoolean());
    }


    [Fact]
    public async Task PutAdoCredentials_MissingField_Returns400()
    {
        var clientId = factory.ClientId;
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/clients/{clientId}/ado-credentials");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
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
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
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
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Content = JsonContent.Create(
            new
            {
                tenantId = "tenant-id-abc",
                clientId = "client-id-abc",
                secret = "super-secret-value",
            });

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var record = await db.Clients.FirstAsync(c => c.Id == clientId);
        Assert.NotEqual("super-secret-value", record.AdoClientSecret);
    }

    [Fact]
    public async Task PutAdoCredentials_FollowedByGetClient_DoesNotExposeSecret()
    {
        var clientId = factory.ClientId;
        var client = factory.CreateClient();

        using (var putRequest = new HttpRequestMessage(HttpMethod.Put, $"/clients/{clientId}/ado-credentials"))
        {
            putRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
            putRequest.Content = JsonContent.Create(new
            {
                tenantId = "tenant-id-abc",
                clientId = "client-id-abc",
                secret = "super-secret-value",
            });

            var putResponse = await client.SendAsync(putRequest);
            Assert.Equal(HttpStatusCode.NoContent, putResponse.StatusCode);
        }

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"/clients/{clientId}");
        getRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var getResponse = await client.SendAsync(getRequest);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var body = await getResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("super-secret-value", body, StringComparison.Ordinal);
        Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("hasAdoCredentials").GetBoolean());
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
            DisplayName = "CSM Test",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Clients.Add(record);
        await db.SaveChangesAsync();

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/clients/{record.Id}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
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
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
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
            DisplayName = "CSM Null Test",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            CustomSystemMessage = "Original message.",
        };
        db.Clients.Add(record);
        await db.SaveChangesAsync();

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/clients/{record.Id}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
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
            DisplayName = "CSM Clear Test",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            CustomSystemMessage = "Will be cleared.",
        };
        db.Clients.Add(record);
        await db.SaveChangesAsync();

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/clients/{record.Id}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Content = JsonContent.Create(new { customSystemMessage = "" });

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(
            body.RootElement.GetProperty("customSystemMessage").ValueKind == JsonValueKind.Null,
            "customSystemMessage should be null after clearing with empty string");
    }


    public sealed class ClientsApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-clients-jwt-secret-32chars!";
        private readonly string _dbName = $"TestDb_Clients_{Guid.NewGuid()}";

        // Explicit root ensures all DbContext instances within this factory share the same in-memory store.
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
            builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
            builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
            builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
            builder.UseSetting("MEISTER_ADMIN_KEY", ValidAdminKey);
            builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);
            // No DB_CONNECTION_STRING → InMemory mode for IJobRepository/IClientRegistry

            var dbName = this._dbName; // capture before lambda
            var dbRoot = this._dbRoot; // capture before lambda
            builder.ConfigureServices(services =>
            {
                // Register IJwtTokenService for JWT Bearer token validation
                services.AddSingleton<IJwtTokenService, JwtTokenService>();

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
                services.AddScoped<IClientAdoOrganizationScopeRepository, ClientAdoOrganizationScopeRepository>();

                // IUserRepository stub (GetClients now injects it for non-admin JWT users)
                var userRepo = Substitute.For<IUserRepository>();
                userRepo.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<Domain.Entities.AppUser?>(null));
                userRepo.GetUserClientRolesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>()));
                services.AddSingleton(userRepo);

                services.AddSingleton(Substitute.For<IClientRegistry>());

                services.AddScoped<IClientAdoCredentialRepository, ClientAdoCredentialRepository>();
                services.AddSingleton(Substitute.For<IJobRepository>());
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = base.CreateHost(builder);

            // Seed the client record so crawl-config endpoints work
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.Clients.Add(
                new ClientRecord
                {
                    Id = this.ClientId,
                    DisplayName = "Test Client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            db.SaveChanges();

            return host;
        }
    }
}

public sealed class ClientsControllerAdoOrganizationScopesTests(
    ClientsControllerAdoOrganizationScopesTests.OrganizationScopesApiFactory factory)
    : IClassFixture<ClientsControllerAdoOrganizationScopesTests.OrganizationScopesApiFactory>
{
    [Fact]
    public async Task GetAdoOrganizationScopes_ClientUserForAssignedClient_Returns200WithScopes()
    {
        var created = await factory.CreateOrganizationScopeAsync();

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.ClientId}/ado-organization-scopes");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientUserToken());

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Single(body.EnumerateArray());

        var scope = body[0];
        Assert.Equal(created.Id, scope.GetProperty("id").GetGuid());
        Assert.Equal(factory.ClientId, scope.GetProperty("clientId").GetGuid());
        Assert.Equal(created.OrganizationUrl, scope.GetProperty("organizationUrl").GetString());
        Assert.Equal(created.DisplayName, scope.GetProperty("displayName").GetString());
        Assert.True(scope.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.String, scope.GetProperty("verificationStatus").ValueKind);
    }

    [Fact]
    public async Task PostAdoOrganizationScope_ClientAdministrator_Returns201AndPersists()
    {
        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/ado-organization-scopes");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());
        request.Content = JsonContent.Create(new
        {
            organizationUrl = "https://dev.azure.com/new-org/",
            displayName = "New Org",
        });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var scopeId = body.GetProperty("id").GetGuid();
        Assert.Equal(factory.ClientId, body.GetProperty("clientId").GetGuid());
        Assert.Equal("https://dev.azure.com/new-org", body.GetProperty("organizationUrl").GetString());
        Assert.Equal("New Org", body.GetProperty("displayName").GetString());
        Assert.True(body.GetProperty("isEnabled").GetBoolean());

        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IClientAdoOrganizationScopeRepository>();
        var persisted = await repository.GetByIdAsync(factory.ClientId, scopeId, CancellationToken.None);
        Assert.NotNull(persisted);
    }

    [Fact]
    public async Task PostAdoOrganizationScope_ClientUser_Returns403()
    {
        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/ado-organization-scopes");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientUserToken());
        request.Content = JsonContent.Create(new
        {
            organizationUrl = "https://dev.azure.com/forbidden-org",
            displayName = "Forbidden Org",
        });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PatchAdoOrganizationScope_ClientAdministrator_UpdatesScope()
    {
        var created = await factory.CreateOrganizationScopeAsync();

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/clients/{factory.ClientId}/ado-organization-scopes/{created.Id}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());
        request.Content = JsonContent.Create(new
        {
            displayName = "Renamed Scope",
            isEnabled = false,
        });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(created.Id, body.GetProperty("id").GetGuid());
        Assert.Equal("Renamed Scope", body.GetProperty("displayName").GetString());
        Assert.False(body.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(created.OrganizationUrl, body.GetProperty("organizationUrl").GetString());
    }

    [Fact]
    public async Task DeleteAdoOrganizationScope_Admin_Returns204AndRemovesScope()
    {
        var created = await factory.CreateOrganizationScopeAsync();

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/clients/{factory.ClientId}/ado-organization-scopes/{created.Id}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateAdminToken());

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IClientAdoOrganizationScopeRepository>();
        var persisted = await repository.GetByIdAsync(factory.ClientId, created.Id, CancellationToken.None);
        Assert.Null(persisted);
    }

    public sealed class OrganizationScopesApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-ado-scopes-jwt-secret-32chars!";

        private readonly string _dbName = $"TestDb_AdoScopes_{Guid.NewGuid()}";
        private readonly InMemoryDatabaseRoot _dbRoot = new();

        public Guid ClientId { get; } = Guid.NewGuid();
        public Guid OtherClientId { get; } = Guid.NewGuid();
        public Guid ClientAdministratorUserId { get; } = Guid.NewGuid();
        public Guid ClientUserId { get; } = Guid.NewGuid();

        public string GenerateAdminToken() => this.GenerateToken(Guid.NewGuid(), AppUserRole.Admin);

        public string GenerateClientAdministratorToken() => this.GenerateToken(this.ClientAdministratorUserId, AppUserRole.User);

        public string GenerateClientUserToken() => this.GenerateToken(this.ClientUserId, AppUserRole.User);

        public async Task<ClientAdoOrganizationScopeDto> CreateOrganizationScopeAsync(
            string? organizationUrl = null,
            string? displayName = "Test Org")
        {
            using var scope = this.Services.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IClientAdoOrganizationScopeRepository>();
            var resolvedOrganizationUrl = organizationUrl ?? $"https://dev.azure.com/test-org-{Guid.NewGuid():N}/";
            return (await repository.AddAsync(this.ClientId, resolvedOrganizationUrl, displayName, CancellationToken.None))!;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
            builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

            var dbName = this._dbName;
            var dbRoot = this._dbRoot;
            var clientId = this.ClientId;
            var clientAdministratorUserId = this.ClientAdministratorUserId;
            var clientUserId = this.ClientUserId;

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IJwtTokenService, JwtTokenService>();
                services.AddDbContext<MeisterProPRDbContext>(options =>
                    options.UseInMemoryDatabase(dbName, dbRoot));
                services.AddDbContextFactory<MeisterProPRDbContext>(options =>
                    options.UseInMemoryDatabase(dbName, dbRoot));
                services.AddScoped<IClientAdminService, ClientAdminService>();
                services.AddScoped<IClientAdoOrganizationScopeRepository, ClientAdoOrganizationScopeRepository>();

                services.AddSingleton(Substitute.For<IAdoTokenValidator>());
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedPrFetcher>());
                services.AddSingleton(Substitute.For<IClientRegistry>());

                var userRepo = Substitute.For<IUserRepository>();
                userRepo.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AppUser?>(null));
                userRepo.GetUserClientRolesAsync(clientAdministratorUserId, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>
                    {
                        { clientId, ClientRole.ClientAdministrator },
                    }));
                userRepo.GetUserClientRolesAsync(clientUserId, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>
                    {
                        { clientId, ClientRole.ClientUser },
                    }));
                userRepo.GetUserClientRolesAsync(
                        Arg.Is<Guid>(id => id != clientAdministratorUserId && id != clientUserId),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>()));
                services.AddSingleton(userRepo);

                var crawlRepo = Substitute.For<ICrawlConfigurationRepository>();
                crawlRepo.GetAllActiveAsync(Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([]));
                services.AddSingleton(crawlRepo);

                var adoCredentialRepository = Substitute.For<IClientAdoCredentialRepository>();
                adoCredentialRepository.GetByClientIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<ClientAdoCredentials?>(null));
                adoCredentialRepository.UpsertAsync(Arg.Any<Guid>(), Arg.Any<ClientAdoCredentials>(), Arg.Any<CancellationToken>())
                    .Returns(Task.CompletedTask);
                adoCredentialRepository.ClearAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.CompletedTask);
                services.AddSingleton(adoCredentialRepository);
                services.AddSingleton(Substitute.For<IJobRepository>());
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = base.CreateHost(builder);

            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.Clients.AddRange(
                new ClientRecord
                {
                    Id = this.ClientId,
                    DisplayName = "ADO Scope Client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                },
                new ClientRecord
                {
                    Id = this.OtherClientId,
                    DisplayName = "Other Client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            db.SaveChanges();

            return host;
        }

        private string GenerateToken(Guid userId, AppUserRole role)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
            var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var token = new JwtSecurityToken(
                issuer: "meisterpropr",
                audience: "meisterpropr",
                claims:
                [
                    new Claim("sub", userId.ToString()),
                    new Claim("global_role", role.ToString()),
                ],
                expires: DateTime.UtcNow.AddHours(1),
                signingCredentials: credentials);

            return new JwtSecurityTokenHandler().WriteToken(token);
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

    /// <summary>
    ///     T020 [US2]: A JWT-authenticated ClientUser (non-admin) must not be able to create clients.
    ///     Fails until T024 changes CreateClient from 401 → 403 for authenticated non-admin users.
    /// </summary>
    [Fact]
    public async Task ClientUser_CannotAccess_ClientManagement_Returns403()
    {
        var http = factory.CreateClient();
        var token = factory.GenerateToken(factory.TestUserId, AppUserRole.User);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/clients");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Content = System.Net.Http.Json.JsonContent.Create(
            new { displayName = "Should Fail" });

        var response = await http.SendAsync(request);

        // Authenticated users without admin role should get 403, not 401
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    public sealed class ClientsJwtApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test_jwt_secret_that_is_32_chars!";
        private const string ValidAdminKey = "admin-key-min-16-chars-ok";

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
            builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
            builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
            builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
            builder.UseSetting("MEISTER_ADMIN_KEY", ValidAdminKey);
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
                services.AddScoped<IClientAdoOrganizationScopeRepository, ClientAdoOrganizationScopeRepository>();

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
                userRepo.GetUserClientRolesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>()));
                services.AddSingleton(userRepo);

                services.AddSingleton(Substitute.For<IClientRegistry>());

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

            // Seed 3 clients
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.Clients.AddRange(
                new ClientRecord { Id = this.ClientAId, DisplayName = "Client A", IsActive = true, CreatedAt = DateTimeOffset.UtcNow },
                new ClientRecord { Id = this.ClientBId, DisplayName = "Client B", IsActive = true, CreatedAt = DateTimeOffset.UtcNow },
                new ClientRecord { Id = this.ClientCId, DisplayName = "Client C", IsActive = true, CreatedAt = DateTimeOffset.UtcNow });
            db.SaveChanges();

            return host;
        }
    }
}
