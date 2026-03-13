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
///     reviewer-identity endpoint.
/// </summary>
public sealed class ClientsControllerReviewerTests(ClientsControllerReviewerTests.ReviewerApiFactory factory)
    : IClassFixture<ClientsControllerReviewerTests.ReviewerApiFactory>
{
    private const string ValidAdminKey = "admin-key-min-16-chars-ok";
    private const string ValidClientKey = "client-key-min-16-chars-ok";

    [Fact]
    public async Task GetClient_AfterReviewerSet_ReviewerIdIsReturned()
    {
        var reviewerId = Guid.NewGuid();

        // Seed a separate client with a reviewer already set
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.Clients.Add(
                new ClientRecord
                {
                    Id = Guid.NewGuid(),
                    Key = $"reviewer-set-key-{Guid.NewGuid():N}",
                    DisplayName = "With Reviewer",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    ReviewerId = reviewerId,
                });
            await db.SaveChangesAsync();
        }

        // Find the ID of the newly seeded client
        Guid seededId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            seededId = await db.Clients
                .Where(c => c.ReviewerId == reviewerId)
                .Select(c => c.Id)
                .FirstAsync();
        }

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/clients/{seededId}");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);

        var response = await httpClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.TryGetProperty("reviewerId", out var prop));
        Assert.Equal(reviewerId.ToString(), prop.GetString());
    }

    // T018 — GET /clients/{id} includes reviewerId (null before set, non-null after)

    [Fact]
    public async Task GetClient_BeforeReviewerSet_ReviewerIdIsNull()
    {
        var clientId = factory.ClientId;
        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/clients/{clientId}");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);

        var response = await httpClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.TryGetProperty("reviewerId", out var prop));
        Assert.Equal(JsonValueKind.Null, prop.ValueKind);
    }

    // T020 — POST crawl-config without reviewerDisplayName succeeds

    [Fact]
    public async Task PostCrawlConfig_WithoutReviewerDisplayName_Returns201()
    {
        var clientId = factory.ClientId;
        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/clients/{clientId}/crawl-configurations");
        request.Headers.Add("X-Client-Key", ValidClientKey);
        request.Content = JsonContent.Create(
            new
            {
                organizationUrl = "https://dev.azure.com/myorg",
                projectId = "MyProject",
                crawlIntervalSeconds = 60,
                // No reviewerDisplayName — this field must no longer be required
            });

        var response = await httpClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // T019 — PUT with Guid.Empty returns 400

    [Fact]
    public async Task PutReviewerIdentity_EmptyGuid_Returns400()
    {
        var clientId = factory.ClientId;
        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/clients/{clientId}/reviewer-identity");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Content = JsonContent.Create(new { reviewerId = Guid.Empty });

        var response = await httpClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PutReviewerIdentity_UnknownClient_Returns404()
    {
        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/clients/{Guid.NewGuid()}/reviewer-identity");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Content = JsonContent.Create(new { reviewerId = Guid.NewGuid() });

        var response = await httpClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // T017 — PUT /clients/{id}/reviewer-identity with valid GUID returns 204 and persists

    [Fact]
    public async Task PutReviewerIdentity_ValidGuid_Returns204AndPersists()
    {
        var clientId = factory.ClientId;
        var reviewerId = Guid.NewGuid();

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/clients/{clientId}/reviewer-identity");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Content = JsonContent.Create(new { reviewerId });

        var response = await httpClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify persisted
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var stored = await db.Clients.FindAsync(clientId);
        Assert.Equal(reviewerId, stored!.ReviewerId);
    }

    [Fact]
    public async Task PutReviewerIdentity_WithoutAdminKey_Returns401()
    {
        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/clients/{factory.ClientId}/reviewer-identity");
        request.Content = JsonContent.Create(new { reviewerId = Guid.NewGuid() });

        var response = await httpClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Factory ───────────────────────────────────────────────────────────────

    public sealed class ReviewerApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = $"TestDb_Reviewer_{Guid.NewGuid()}";
        private readonly InMemoryDatabaseRoot _dbRoot = new();

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
            var clientId = this.ClientId;

            builder.ConfigureServices(services =>
            {
                services.AddSingleton(Substitute.For<IAdoTokenValidator>());
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedPullRequestFetcher>());

                services.AddDbContext<MeisterProPRDbContext>(opts =>
                    opts.UseInMemoryDatabase(dbName, dbRoot));

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
                services.AddSingleton(crawlRepo);

                var clientRegistry = Substitute.For<IClientRegistry>();
                clientRegistry.IsValidKey(ValidClientKey).Returns(true);
                clientRegistry.GetClientIdByKeyAsync(ValidClientKey, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<Guid?>(clientId));
                clientRegistry.GetClientIdByKeyAsync(
                        Arg.Is<string>(k => k != ValidClientKey),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<Guid?>(null));
                services.AddSingleton(clientRegistry);

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

            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.Clients.Add(
                new ClientRecord
                {
                    Id = this.ClientId,
                    Key = ValidClientKey,
                    DisplayName = "Reviewer Test Client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            db.SaveChanges();

            return host;
        }
    }
}