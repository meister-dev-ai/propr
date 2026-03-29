using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
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
///     Integration tests for <see cref="MeisterProPR.Api.Controllers.FindingDismissalsController" />.
///     T025 — covers list, create, update label, and delete dismissals.
/// </summary>
public sealed class FindingDismissalsControllerTests(FindingDismissalsControllerTests.DismissalsApiFactory factory)
    : IClassFixture<FindingDismissalsControllerTests.DismissalsApiFactory>
{
    private const string ValidAdminKey = "admin-key-min-16-chars-ok";
    private const string ValidClientKey = "client-key-min-16-chars-ok";

    // ─── helpers ────────────────────────────────────────────────────────────────

    /// <summary>Creates a dismissal via POST and returns its id.</summary>
    private async Task<Guid> SeedDismissalAsync(string message = "Variable is unused", string? label = null)
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/finding-dismissals");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Content = JsonContent.Create(new { originalMessage = message, label });

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetGuid();
    }

    // ─── GET /clients/{clientId}/finding-dismissals ──────────────────────────────

    [Fact]
    public async Task ListDismissals_WithAdminKey_Returns200()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.ClientId}/finding-dismissals");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ListDismissals_WithoutAdminKey_Returns401()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.ClientId}/finding-dismissals");

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ListDismissals_ReturnsCreatedDismissals()
    {
        await this.SeedDismissalAsync("Some unused variable warning");

        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/clients/{factory.ClientId}/finding-dismissals");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.GetArrayLength() >= 1);
    }

    // ─── POST /clients/{clientId}/finding-dismissals ─────────────────────────────

    [Fact]
    public async Task CreateDismissal_WithValidPayload_Returns201WithDto()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/finding-dismissals");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Content = JsonContent.Create(new
        {
            originalMessage = "Consider renaming this variable to improve clarity",
            label = "Noise: naming style",
        });

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.TryGetProperty("id", out _));
        Assert.Equal(factory.ClientId, body.GetProperty("clientId").GetGuid());
        Assert.Equal("Noise: naming style", body.GetProperty("label").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("patternText").GetString()));
    }

    [Fact]
    public async Task CreateDismissal_MissingOriginalMessage_Returns400()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/finding-dismissals");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Content = JsonContent.Create(new { label = "some label" });

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateDismissal_EmptyOriginalMessage_Returns400()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/finding-dismissals");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Content = JsonContent.Create(new { originalMessage = "   " });

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateDismissal_WithoutAdminKey_Returns401()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/finding-dismissals");
        request.Content = JsonContent.Create(new { originalMessage = "Some message" });

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ─── PATCH /clients/{clientId}/finding-dismissals/{id} ───────────────────────

    [Fact]
    public async Task UpdateDismissal_WithValidLabel_Returns200WithUpdatedDto()
    {
        var id = await this.SeedDismissalAsync("Method is too long");

        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/clients/{factory.ClientId}/finding-dismissals/{id}");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Content = JsonContent.Create(new { label = "Acknowledged: method length" });

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Acknowledged: method length", body.GetProperty("label").GetString());
    }

    [Fact]
    public async Task UpdateDismissal_UnknownId_Returns404()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/clients/{factory.ClientId}/finding-dismissals/{Guid.NewGuid()}");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Content = JsonContent.Create(new { label = "Updated label" });

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateDismissal_WithoutAdminKey_Returns401()
    {
        var id = await this.SeedDismissalAsync("Unused import statement");

        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/clients/{factory.ClientId}/finding-dismissals/{id}");
        request.Content = JsonContent.Create(new { label = "No auth" });

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ─── DELETE /clients/{clientId}/finding-dismissals/{id} ──────────────────────

    [Fact]
    public async Task DeleteDismissal_ExistingId_Returns204()
    {
        var id = await this.SeedDismissalAsync("Low-value suggestion about whitespace");

        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/clients/{factory.ClientId}/finding-dismissals/{id}");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task DeleteDismissal_UnknownId_Returns404()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/clients/{factory.ClientId}/finding-dismissals/{Guid.NewGuid()}");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteDismissal_WithoutAdminKey_Returns401()
    {
        var id = await this.SeedDismissalAsync("Another low-value suggestion");

        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/clients/{factory.ClientId}/finding-dismissals/{id}");

        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ─── WebApplicationFactory ───────────────────────────────────────────────────

    public sealed class DismissalsApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = $"TestDb_Dismissals_{Guid.NewGuid()}";
        private readonly InMemoryDatabaseRoot _dbRoot = new();

        /// <summary>The UUID of the seeded client used by all tests in this fixture.</summary>
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

                // InMemory EF Core DB (shared across all requests in this factory instance)
                services.AddDbContext<MeisterProPRDbContext>(opts =>
                    opts.UseInMemoryDatabase(dbName, dbRoot));

                // Real repository backed by InMemory DB
                services.AddScoped<IFindingDismissalRepository, FindingDismissalRepository>();

                // Stub IClientRegistry
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

                // Stub remaining dependencies the app expects
                var userRepo = Substitute.For<IUserRepository>();
                userRepo.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<MeisterProPR.Domain.Entities.AppUser?>(null));
                services.AddSingleton(userRepo);

                var crawlRepo = Substitute.For<ICrawlConfigurationRepository>();
                crawlRepo.GetAllActiveAsync(Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<MeisterProPR.Application.DTOs.CrawlConfigurationDto>>([]));
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

            // Seed the client record so the controller can recognise the clientId in the route
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.Clients.Add(new ClientRecord
            {
                Id = this.ClientId,
                Key = ValidClientKey,
                DisplayName = "Dismissals Test Client",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();

            return host;
        }
    }
}
