using System.Net;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace MeisterProPR.Api.Tests;

/// <summary>
///     DI-wiring smoke tests that verify controller endpoints respond successfully,
///     catching failures that /healthz alone would not surface (e.g., missing logger
///     registrations or broken controller dependency graphs).
/// </summary>
public sealed class ControllerSmokeTests(ControllerSmokeTests.SmokeFactory factory)
    : IClassFixture<ControllerSmokeTests.SmokeFactory>
{
    private const string ValidAdminKey = "smoke-admin-key-min-16-chars";
    private const string ValidClientKey = "smoke-client-key-min-16-chars";

    /// <summary>
    ///     Verifies that GET /clients resolves the full controller DI graph (including
    ///     <see cref="Microsoft.Extensions.Logging.ILogger{T}" />) and returns 200.
    ///     A broken dependency — such as a missing logger — causes a 500 rather than
    ///     a startup failure, so this test catches regressions that /healthz cannot.
    /// </summary>
    [Fact]
    public async Task GetClients_WithAdminKey_Returns200()
    {
        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/clients");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    ///     Verifies that GET /clients rejects requests missing the admin key,
    ///     confirming authentication middleware is active on controller endpoints.
    /// </summary>
    [Fact]
    public async Task GetClients_WithoutAdminKey_Returns401()
    {
        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/clients");

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    ///     <see cref="WebApplicationFactory{TEntryPoint}" /> configured for smoke testing.
    ///     Uses an isolated in-memory database and stubs all external dependencies so the
    ///     full DI graph can be resolved without external services.
    /// </summary>
    public sealed class SmokeFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = $"TestDb_Smoke_{Guid.NewGuid()}";
        private readonly InMemoryDatabaseRoot _dbRoot = new();

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
                services.AddSingleton(Substitute.For<IAdoTokenValidator>());
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedPrFetcher>());

                services.AddDbContext<MeisterProPRDbContext>(opts =>
                    opts.UseInMemoryDatabase(dbName, dbRoot));
                services.AddScoped<IClientAdminService, ClientAdminService>();

                var crawlRepo = Substitute.For<ICrawlConfigurationRepository>();
                services.AddSingleton(crawlRepo);

                var clientRegistry = Substitute.For<IClientRegistry>();
                clientRegistry.IsValidKey(ValidClientKey).Returns(true);
                clientRegistry.GetClientIdByKeyAsync(ValidClientKey, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<Guid?>(Guid.NewGuid()));
                services.AddSingleton(clientRegistry);

                var adoCredRepo = Substitute.For<IClientAdoCredentialRepository>();
                services.AddSingleton(adoCredRepo);
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = base.CreateHost(builder);

            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.Database.EnsureCreated();

            return host;
        }
    }
}
