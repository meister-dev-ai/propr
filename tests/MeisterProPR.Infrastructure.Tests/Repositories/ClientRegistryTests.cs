// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Security;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Services;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using MeisterProPR.Infrastructure.Tests.GitHub;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Tests for <see cref="DbClientRegistry" /> reviewer identity lookups.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class ClientRegistryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private readonly List<Guid> _seededClientIds = [];
    private ClientScmConnectionRepository _connectionRepository = null!;
    private MeisterProPRDbContext _dbContext = null!;
    private IHttpClientFactory _httpClientFactory = null!;
    private DbClientRegistry _registry = null!;
    private ClientReviewerIdentityRepository _reviewerIdentityRepository = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);
        var codec = CreateCodec();
        this._connectionRepository = new ClientScmConnectionRepository(this._dbContext, codec);
        this._reviewerIdentityRepository = new ClientReviewerIdentityRepository(this._dbContext);
        this._httpClientFactory = Substitute.For<IHttpClientFactory>();
        this._httpClientFactory.CreateClient("GitHubProvider")
            .Returns(
                new HttpClient(
                    new StubHttpMessageHandler(request => Task.FromResult(
                        request.RequestUri!.AbsoluteUri switch
                        {
                            "https://api.github.com/app/installations/789012" => CreateJsonResponse(
                                new { account = new { login = "meister-dev-ai" }, app_slug = "propr-review" }),
                            "https://api.github.com/app" => CreateJsonResponse(new { slug = "propr-review", name = "ProPR Review" }),
                            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                        }))));
        this._registry = new DbClientRegistry(
            this._dbContext,
            this._connectionRepository,
            this._reviewerIdentityRepository,
            async (host, connection, ct) =>
            {
                var authenticationService = new GitHubAuthenticationService(this._httpClientFactory);
                var app = await authenticationService.GetAppMetadataAsync(host, connection, ct);
                var login = app.Slug + "[bot]";
                return new ReviewerIdentity(host, login, login, app.DisplayName, true);
            });
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is not null)
        {
            if (this._seededClientIds.Count > 0)
            {
                await this._dbContext.ClientReviewerIdentities
                    .Where(identity => this._seededClientIds.Contains(identity.ClientId))
                    .ExecuteDeleteAsync();
                await this._dbContext.ClientScmScopes
                    .Where(scope => this._seededClientIds.Contains(scope.ClientId))
                    .ExecuteDeleteAsync();
                await this._dbContext.ClientScmConnections
                    .Where(connection => this._seededClientIds.Contains(connection.ClientId))
                    .ExecuteDeleteAsync();
                await this._dbContext.Clients
                    .Where(client => this._seededClientIds.Contains(client.Id))
                    .ExecuteDeleteAsync();
            }

            await this._dbContext.DisposeAsync();
        }
    }

    private static ISecretProtectionCodec CreateCodec()
    {
        var keysDirectory = Path.Combine(Path.GetTempPath(), $"MeisterProPR.ClientRegistryTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysDirectory);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("MeisterProPR.Tests")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

        var provider = services.BuildServiceProvider();
        return new SecretProtectionCodec(provider.GetRequiredService<IDataProtectionProvider>());
    }

    [Fact]
    public async Task GetReviewerIdentityAsync_ActiveConnectionWithConfiguredIdentity_ReturnsReviewerIdentity()
    {
        var client = await this.SeedClientAsync();
        var connection = await this._connectionRepository.AddAsync(
            client.Id,
            ScmProvider.GitHub,
            "https://github.com",
            ScmAuthenticationKind.PersonalAccessToken,
            "GitHub",
            "ghp_test",
            true,
            CancellationToken.None);

        Assert.NotNull(connection);

        await this._reviewerIdentityRepository.UpsertAsync(
            client.Id,
            connection!.Id,
            ScmProvider.GitHub,
            "12345",
            "meister-review-bot[bot]",
            "Meister Review Bot",
            true,
            CancellationToken.None);

        var result = await this._registry.GetReviewerIdentityAsync(
            client.Id,
            new ProviderHostRef(ScmProvider.GitHub, "https://github.com"),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("12345", result!.ExternalUserId);
        Assert.Equal("meister-review-bot[bot]", result.Login);
        Assert.Equal("Meister Review Bot", result.DisplayName);
        Assert.True(result.IsBot);
    }

    [Fact]
    public async Task GetReviewerIdentityAsync_UnknownHost_ReturnsNull()
    {
        var client = await this.SeedClientAsync();

        var result = await this._registry.GetReviewerIdentityAsync(
            client.Id,
            new ProviderHostRef(ScmProvider.GitHub, "https://github.example.com"),
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetReviewerIdentityAsync_ConnectionWithoutReviewerIdentity_ReturnsNull()
    {
        var client = await this.SeedClientAsync();
        await this._connectionRepository.AddAsync(
            client.Id,
            ScmProvider.GitHub,
            "https://github.com",
            ScmAuthenticationKind.PersonalAccessToken,
            "GitHub",
            "ghp_test",
            true,
            CancellationToken.None);

        var result = await this._registry.GetReviewerIdentityAsync(
            client.Id,
            new ProviderHostRef(ScmProvider.GitHub, "https://github.com"),
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetEffectiveReviewerIdentityAsync_GitHubAppConnectionWithoutConfiguredIdentity_ReturnsDerivedAppIdentity()
    {
        var client = await this.SeedClientAsync();
        await this._connectionRepository.AddAsync(
            client.Id,
            ScmProvider.GitHub,
            "https://github.com",
            ScmAuthenticationKind.AppInstallation,
            null,
            null,
            "GitHub App",
            GitHubAppTestHelpers.CreatePrivateKeyPem(true),
            true,
            123456,
            789012,
            ct: CancellationToken.None);

        var result = await this._registry.GetEffectiveReviewerIdentityAsync(
            client.Id,
            new ProviderHostRef(ScmProvider.GitHub, "https://github.com"),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("propr-review[bot]", result!.Login);
        Assert.Equal("ProPR Review", result.DisplayName);
        Assert.True(result.IsBot);
    }

    [Fact]
    public async Task UpsertReviewerIdentity_DoesNotMutateConnectionCredentials()
    {
        var client = await this.SeedClientAsync();
        var connection = await this._connectionRepository.AddAsync(
            client.Id,
            ScmProvider.GitHub,
            "https://github.com",
            ScmAuthenticationKind.PersonalAccessToken,
            "GitHub",
            "ghp_secret_before",
            true,
            CancellationToken.None);

        Assert.NotNull(connection);

        var credentialBefore = await this._connectionRepository.GetOperationalConnectionAsync(
            client.Id,
            new ProviderHostRef(ScmProvider.GitHub, "https://github.com"),
            CancellationToken.None);

        await this._reviewerIdentityRepository.UpsertAsync(
            client.Id,
            connection!.Id,
            ScmProvider.GitHub,
            "12345",
            "meister-review-bot[bot]",
            "Meister Review Bot",
            true,
            CancellationToken.None);

        var credentialAfter = await this._connectionRepository.GetOperationalConnectionAsync(
            client.Id,
            new ProviderHostRef(ScmProvider.GitHub, "https://github.com"),
            CancellationToken.None);

        Assert.NotNull(credentialBefore);
        Assert.NotNull(credentialAfter);
        Assert.Equal(connection.Id, credentialAfter!.Id);
        Assert.Equal(credentialBefore!.Secret, credentialAfter.Secret);
        Assert.Equal(credentialBefore.AuthenticationKind, credentialAfter.AuthenticationKind);
        Assert.Equal(credentialBefore.OAuthClientId, credentialAfter.OAuthClientId);
        Assert.Equal(credentialBefore.OAuthTenantId, credentialAfter.OAuthTenantId);
        Assert.Equal(credentialBefore.GitHubAppId, credentialAfter.GitHubAppId);
        Assert.Equal(credentialBefore.GitHubAppInstallationId, credentialAfter.GitHubAppInstallationId);
    }

    [Fact]
    public async Task DeleteReviewerIdentity_DoesNotMutateConnectionCredentials()
    {
        var client = await this.SeedClientAsync();
        var connection = await this._connectionRepository.AddAsync(
            client.Id,
            ScmProvider.GitHub,
            "https://github.com",
            ScmAuthenticationKind.PersonalAccessToken,
            "GitHub",
            "ghp_secret_before",
            true,
            CancellationToken.None);

        Assert.NotNull(connection);

        await this._reviewerIdentityRepository.UpsertAsync(
            client.Id,
            connection!.Id,
            ScmProvider.GitHub,
            "12345",
            "meister-review-bot[bot]",
            "Meister Review Bot",
            true,
            CancellationToken.None);

        var credentialBeforeDelete = await this._connectionRepository.GetOperationalConnectionAsync(
            client.Id,
            new ProviderHostRef(ScmProvider.GitHub, "https://github.com"),
            CancellationToken.None);

        var deleted = await this._reviewerIdentityRepository.DeleteAsync(
            client.Id,
            connection.Id,
            CancellationToken.None);

        var credentialAfterDelete = await this._connectionRepository.GetOperationalConnectionAsync(
            client.Id,
            new ProviderHostRef(ScmProvider.GitHub, "https://github.com"),
            CancellationToken.None);

        Assert.True(deleted);
        Assert.NotNull(credentialBeforeDelete);
        Assert.NotNull(credentialAfterDelete);
        Assert.Equal(connection.Id, credentialAfterDelete!.Id);
        Assert.Equal(credentialBeforeDelete!.Secret, credentialAfterDelete.Secret);
        Assert.Equal(credentialBeforeDelete.AuthenticationKind, credentialAfterDelete.AuthenticationKind);
        Assert.Equal(credentialBeforeDelete.OAuthClientId, credentialAfterDelete.OAuthClientId);
        Assert.Equal(credentialBeforeDelete.OAuthTenantId, credentialAfterDelete.OAuthTenantId);
        Assert.Equal(credentialBeforeDelete.GitHubAppId, credentialAfterDelete.GitHubAppId);
        Assert.Equal(credentialBeforeDelete.GitHubAppInstallationId, credentialAfterDelete.GitHubAppInstallationId);
    }

    [Fact]
    public async Task GetScmCommentPostingEnabledAsync_ClientWithSetting_ReturnsPersistedValue()
    {
        var client = await this.SeedClientAsync();
        client.ScmCommentPostingEnabled = false;
        await this._dbContext.SaveChangesAsync();

        var result = await this._registry.GetScmCommentPostingEnabledAsync(client.Id, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task GetScmCommentPostingEnabledAsync_UnknownClient_DefaultsToTrue()
    {
        var result = await this._registry.GetScmCommentPostingEnabledAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task GetProRvEnabledAsync_ClientWithSetting_ReturnsPersistedValue()
    {
        var client = await this.SeedClientAsync();
        client.EnableProRV = false;
        await this._dbContext.SaveChangesAsync();

        var result = await this._registry.GetProRvEnabledAsync(client.Id, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task GetProRvEnabledAsync_UnknownClient_DefaultsToFalse()
    {
        var result = await this._registry.GetProRvEnabledAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task DefaultReviewPipelineProfileId_RoundTripsNullableValueAcrossPersistence()
    {
        var client = await this.SeedClientAsync();
        var adminService = new ClientAdminService(this._dbContext);

        client.DefaultReviewPipelineProfileId = ReviewPipelineProfileProvider.FileByFileAssertiveProfileId;
        client.DefaultReviewPipelineProfileUpdatedAtUtc = DateTimeOffset.UtcNow;
        await this._dbContext.SaveChangesAsync();

        var persistedProfileId = await this._registry.GetDefaultReviewPipelineProfileIdAsync(client.Id, CancellationToken.None);
        var persistedClient = await adminService.GetByIdAsync(client.Id, CancellationToken.None);

        Assert.Equal(ReviewPipelineProfileProvider.FileByFileAssertiveProfileId, persistedProfileId);
        Assert.NotNull(persistedClient);
        Assert.Equal(ReviewPipelineProfileProvider.FileByFileAssertiveProfileId, persistedClient!.DefaultReviewPipelineProfileId);
        Assert.NotNull(persistedClient.DefaultReviewPipelineProfileUpdatedAtUtc);

        client.DefaultReviewPipelineProfileId = null;
        client.DefaultReviewPipelineProfileUpdatedAtUtc = null;
        await this._dbContext.SaveChangesAsync();

        var clearedProfileId = await this._registry.GetDefaultReviewPipelineProfileIdAsync(client.Id, CancellationToken.None);
        var clearedClient = await adminService.GetByIdAsync(client.Id, CancellationToken.None);

        Assert.Null(clearedProfileId);
        Assert.NotNull(clearedClient);
        Assert.Null(clearedClient!.DefaultReviewPipelineProfileId);
        Assert.Null(clearedClient.DefaultReviewPipelineProfileUpdatedAtUtc);
    }

    private async Task<ClientRecord> SeedClientAsync()
    {
        var record = new ClientRecord
        {
            Id = Guid.NewGuid(),
            TenantId = TenantCatalog.SystemTenantId,
            DisplayName = "Test Client",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        this._dbContext.Clients.Add(record);
        await this._dbContext.SaveChangesAsync();
        this._seededClientIds.Add(record.Id);
        return record;
    }

    private static HttpResponseMessage CreateJsonResponse<T>(T payload)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload)),
        };
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return responder(request);
        }
    }
}
