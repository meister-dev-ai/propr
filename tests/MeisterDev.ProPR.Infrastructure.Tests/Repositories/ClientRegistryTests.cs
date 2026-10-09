// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Infrastructure.Services;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;
using MeisterDev.ProPR.Infrastructure.Tests.GitHub;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using FactAttribute = Xunit.SkippableFactAttribute;
using MeisterDev.ProPR.TestSupport;
using MeisterDev.ProPR.Infrastructure.Features.Clients;
using MeisterDev.ProPR.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Identity;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Tests for <see cref="DbClientRegistry" /> reviewer identity lookups.
/// </summary>
[Collection("PostgresIntegration3")]
public sealed class ClientRegistryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private readonly List<Guid> _seededClientIds = [];
    private ClientScmConnectionRepository _connectionRepository = null!;
    private MeisterProPRDbContext _dbContext = null!;
    private IHttpClientFactory _httpClientFactory = null!;
    private DbClientRegistry _registry = null!;
    private ClientReviewerIdentityRepository _reviewerIdentityRepository = null!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientsCompositionDerivesSelectedAppIdentityWithoutConstructingOtherNativeServices(bool provideUnrelatedDependency)
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var connection = new ClientScmConnectionCredentialDto(
            Guid.NewGuid(), clientId, ScmProvider.GitHub,
            host.HostBaseUrl, ScmAuthenticationKind.AppInstallation, null, null, "GitHub App",
            GitHubAppTestHelpers.CreatePrivateKeyPem(true), true, AppId: 123456, InstallationId: 789012);
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>()).Returns(connection);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["DB_CONNECTION_STRING"] = fixture.ConnectionString }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddInfrastructureSupport(configuration, includeProviderOperationalServices: false);
        services.AddClientsModule(configuration);
        services.AddSingleton(connections);
        services.AddSingleton(Substitute.For<IClientReviewerIdentityRepository>());
        services.AddSingleton(this._httpClientFactory);
        if (provideUnrelatedDependency)
        {
            services.AddSingleton(Substitute.For<IIdentityResolver>());
        }

        var unrelatedConstructed = false;
        services.AddScoped<IReviewerIdentityService>(_ =>
        {
            unrelatedConstructed = true;
            throw new InvalidOperationException("Unrelated native identity service must not be constructed.");
        });
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<IClientRegistry>()
            .GetEffectiveReviewerIdentityAsync(clientId, host);

        Assert.False(unrelatedConstructed);
        Assert.NotNull(result);
        Assert.Equal("propr-review[bot]", result.Login);
        await connections.Received(2).GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ScmProvider.AzureDevOps)]
    [InlineData(ScmProvider.GitLab)]
    [InlineData(ScmProvider.Forgejo)]
    public async Task AutomaticIdentity_IneligibleProviderDoesNotRepeatConnectionLookup(ScmProvider provider)
    {
        var host = new ProviderHostRef(provider, "https://scm.example");
        var clientId = Guid.NewGuid();
        var connections = Substitute.For<IClientScmConnectionRepository>();
        var identities = Substitute.For<IClientReviewerIdentityRepository>();
        var connection = new ClientScmConnectionCredentialDto(
            Guid.NewGuid(), clientId, provider,
            host.HostBaseUrl, ScmAuthenticationKind.PersonalAccessToken, "SCM", "test", true);
        connections.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>()).Returns(connection);
        var derived = false;
        var registry = new DbClientRegistry(
            this._dbContext, connections, identities,
            (_, _, _) =>
            {
                derived = true;
                return Task.FromResult<ReviewerIdentity?>(null);
            });

        Assert.Null(await registry.GetEffectiveReviewerIdentityAsync(clientId, host));

        Assert.False(derived);
        await connections.Received(1).GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>());
        await identities.Received(1).GetByConnectionIdAsync(clientId, connection.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AutomaticIdentity_UsesTheInjectedNativeEligibilityDeclaration()
    {
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://scm.example");
        var clientId = Guid.NewGuid();
        var connections = Substitute.For<IClientScmConnectionRepository>();
        var identities = Substitute.For<IClientReviewerIdentityRepository>();
        var connection = new ClientScmConnectionCredentialDto(
            Guid.NewGuid(), clientId, host.Provider,
            host.HostBaseUrl, ScmAuthenticationKind.PersonalAccessToken, "SCM", "test", true);
        connections.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>()).Returns(connection);
        var policy = Substitute.For<IScmIdentityPolicy>();
        policy.Provider.Returns(host.Provider);
        policy.CanDeriveAutomaticReviewerIdentity.Returns(true);
        policy.IsAutomaticReviewerIdentityEligible(connection.AuthenticationKind).Returns(true);
        var expected = new ReviewerIdentity(host, "native", "native", "Native", true);
        var registry = new DbClientRegistry(
            this._dbContext, connections, identities,
            (_, selected, _) => Task.FromResult<ReviewerIdentity?>(ReferenceEquals(selected, connection) ? expected : null),
            identityPolicies: [policy]);

        Assert.Same(expected, await registry.GetEffectiveReviewerIdentityAsync(clientId, host));
        policy.Received(1).IsAutomaticReviewerIdentityEligible(connection.AuthenticationKind);
        await connections.Received(2).GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>());
    }

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
                var service = new MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Identity.GitHubReviewerIdentityService(
                    new GitHubConnectionVerifier(this._connectionRepository, this._httpClientFactory), this._httpClientFactory);
                return await service.GetAutomaticReviewerIdentityAsync(host, connection, ct);
            });
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is not null)
        {
            if (this._seededClientIds.Count > 0)
            {
                await this._dbContext.ClientReviewPasses
                    .Where(pass => this._seededClientIds.Contains(pass.ClientId))
                    .ExecuteDeleteAsync();
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
        var keysDirectory = Path.Combine(Path.GetTempPath(), $"MeisterDev.ProPR.ClientRegistryTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysDirectory);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("MeisterDev.ProPR.Tests")
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
        Assert.Equal(credentialBefore.AppId, credentialAfter.AppId);
        Assert.Equal(credentialBefore.InstallationId, credentialAfter.InstallationId);
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
        Assert.Equal(credentialBeforeDelete.AppId, credentialAfterDelete.AppId);
        Assert.Equal(credentialBeforeDelete.InstallationId, credentialAfterDelete.InstallationId);
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
    public async Task GetMultiPassUnionEnabledAsync_ClientWithSetting_ReturnsPersistedValue()
    {
        var client = await this.SeedClientAsync();
        client.EnableMultiPassUnion = true;
        await this._dbContext.SaveChangesAsync();

        var result = await this._registry.GetMultiPassUnionEnabledAsync(client.Id, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task GetMultiPassUnionEnabledAsync_UnknownClient_DefaultsToFalse()
    {
        var result = await this._registry.GetMultiPassUnionEnabledAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task GetReviewEveryIncrementEnabledAsync_ClientWithSetting_ReturnsPersistedValue()
    {
        var client = await this.SeedClientAsync();
        client.ReviewEveryIncrementEnabled = true;
        await this._dbContext.SaveChangesAsync();

        var result = await this._registry.GetReviewEveryIncrementEnabledAsync(client.Id, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task GetReviewEveryIncrementEnabledAsync_UnknownClient_DefaultsToFalse()
    {
        var result = await this._registry.GetReviewEveryIncrementEnabledAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task GetWithholdOutOfScopeFindingsAsync_ClientWithSetting_ReturnsPersistedValue()
    {
        var client = await this.SeedClientAsync();
        client.WithholdOutOfScopeFindings = true;
        await this._dbContext.SaveChangesAsync();

        var result = await this._registry.GetWithholdOutOfScopeFindingsAsync(client.Id, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task GetWithholdOutOfScopeFindingsAsync_UnknownClient_DefaultsToFalse()
    {
        var result = await this._registry.GetWithholdOutOfScopeFindingsAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task GetReviewPassesAsync_UnknownClient_ReturnsEmpty()
    {
        var result = await this._registry.GetReviewPassesAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task ReviewPasses_PatchPersistsOrderedListAndGetEchoesIt()
    {
        var client = await this.SeedClientAsync();
        var adminService = new ClientAdminService(this._dbContext, new UnlimitedStockQuotaGate());
        // Each pass must reference a configured model that exists (the FK to ai_configured_models is enforced).
        var modelA = await this.SeedChatModelAsync(client.Id);
        var modelB = await this.SeedChatModelAsync(client.Id);

        // Supplied out of ordinal order; PatchAsync normalizes to a contiguous 0..n order.
        var updated = await adminService.PatchAsync(
            client.Id,
            null,
            null,
            reviewPasses: new List<ReviewPassDto> { new(1, modelB), new(0, modelA) },
            ct: CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(
            new[] { new ReviewPassDto(0, modelA), new ReviewPassDto(1, modelB) },
            updated!.ReviewPassesOrEmpty.ToArray());

        // The registry returns the configured-model ids in ordinal order for the review pipeline.
        var passes = await this._registry.GetReviewPassesAsync(client.Id, CancellationToken.None);
        Assert.Equal(new[] { modelA, modelB }, passes.Select(pass => pass.ConfiguredModelId).ToArray());
        Assert.All(passes, pass => Assert.Null(pass.Lens));
    }

    // A name-based pass row (no configured_model_id) surfaces its LogicalModelName through the registry so
    // the runtime resolves it via the logical-model catalog.
    [Fact]
    public async Task GetReviewPassesAsync_CarriesLogicalModelName_ForNameBasedPass()
    {
        var client = await this.SeedClientAsync();
        this._dbContext.ClientReviewPasses.Add(
            new ClientReviewPassRecord
            {
                Id = Guid.NewGuid(),
                ClientId = client.Id,
                Ordinal = 0,
                ConfiguredModelId = null,
                LogicalModelName = "deep",
            });
        await this._dbContext.SaveChangesAsync();

        var pass = Assert.Single(await this._registry.GetReviewPassesAsync(client.Id, CancellationToken.None));
        Assert.Equal("deep", pass.LogicalModelName);
        Assert.Equal(Guid.Empty, pass.ConfiguredModelId);
    }

    // A name-based pass patched through the admin service persists with no configured-model id
    // and reads back carrying the logical-model name on both the client DTO and the registry spec.
    [Fact]
    public async Task ReviewPasses_NameBasedPass_PersistsAndReadsBack()
    {
        var client = await this.SeedClientAsync();
        var adminService = new ClientAdminService(this._dbContext, new UnlimitedStockQuotaGate());

        var updated = await adminService.PatchAsync(
            client.Id,
            null,
            null,
            reviewPasses: new List<ReviewPassDto> { new(0, Guid.Empty, LogicalModelName: "deep") },
            ct: CancellationToken.None);

        Assert.NotNull(updated);
        var dto = Assert.Single(updated!.ReviewPassesOrEmpty);
        Assert.Equal("deep", dto.LogicalModelName);
        Assert.Equal(Guid.Empty, dto.ConfiguredModelId);

        var pass = Assert.Single(await this._registry.GetReviewPassesAsync(client.Id, CancellationToken.None));
        Assert.Equal("deep", pass.LogicalModelName);
        Assert.Equal(Guid.Empty, pass.ConfiguredModelId);
    }

    [Fact]
    public async Task ReviewPasses_PatchPersistsLens_AndGetEchoesIt()
    {
        var client = await this.SeedClientAsync();
        var adminService = new ClientAdminService(this._dbContext, new UnlimitedStockQuotaGate());
        var resampleModel = await this.SeedChatModelAsync(client.Id);
        var securityModel = await this.SeedChatModelAsync(client.Id);

        var updated = await adminService.PatchAsync(
            client.Id,
            null,
            null,
            reviewPasses: new List<ReviewPassDto> { new(0, resampleModel), new(1, securityModel, "security") },
            ct: CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(
            new[] { new ReviewPassDto(0, resampleModel), new ReviewPassDto(1, securityModel, "security") },
            updated!.ReviewPassesOrEmpty.ToArray());

        // The lens survives to the review-pipeline projection so a lens pass runs the specialist prompt.
        var passes = await this._registry.GetReviewPassesAsync(client.Id, CancellationToken.None);
        Assert.Equal(new[] { null, "security" }, passes.Select(pass => pass.Lens).ToArray());
    }

    [Fact]
    public async Task ReviewPasses_PatchPersistsScopeAndShadow_AndGetEchoesIt()
    {
        var client = await this.SeedClientAsync();
        var adminService = new ClientAdminService(this._dbContext, new UnlimitedStockQuotaGate());
        var perFileModel = await this.SeedChatModelAsync(client.Id);
        var prWideModel = await this.SeedChatModelAsync(client.Id);

        var updated = await adminService.PatchAsync(
            client.Id,
            null,
            null,
            reviewPasses: new List<ReviewPassDto>
            {
                new(0, perFileModel),
                new(1, prWideModel, Scope: "pr_wide", Shadow: true),
            },
            ct: CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(
            new[]
            {
                new ReviewPassDto(0, perFileModel),
                new ReviewPassDto(1, prWideModel, Scope: "pr_wide", Shadow: true),
            },
            updated!.ReviewPassesOrEmpty.ToArray());

        // The scope and shadow survive to the review-pipeline projection.
        var passes = await this._registry.GetReviewPassesAsync(client.Id, CancellationToken.None);
        Assert.Equal(new[] { null, "pr_wide" }, passes.Select(pass => pass.Scope).ToArray());
        Assert.Equal(new[] { false, true }, passes.Select(pass => pass.Shadow).ToArray());
    }

    [Fact]
    public async Task ReviewPasses_PatchPersistsReasoningEffort_AndGetEchoesIt()
    {
        var client = await this.SeedClientAsync();
        var adminService = new ClientAdminService(this._dbContext, new UnlimitedStockQuotaGate());
        var defaultModel = await this.SeedChatModelAsync(client.Id);
        var highModel = await this.SeedChatModelAsync(client.Id);

        var updated = await adminService.PatchAsync(
            client.Id,
            null,
            null,
            reviewPasses: new List<ReviewPassDto>
            {
                new(0, defaultModel),
                new(1, highModel, ReasoningEffort: ReviewReasoningEffort.High),
            },
            ct: CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(
            new[]
            {
                new ReviewPassDto(0, defaultModel, ReasoningEffort: ReviewReasoningEffort.None),
                new ReviewPassDto(1, highModel, ReasoningEffort: ReviewReasoningEffort.High),
            },
            updated!.ReviewPassesOrEmpty.ToArray());

        // The effort survives to the review-pipeline projection so it reaches the outbound request. An unset
        // per-pass effort reads back as None (the column is null in the database).
        var passes = await this._registry.GetReviewPassesAsync(client.Id, CancellationToken.None);
        Assert.Equal(
            new[] { ReviewReasoningEffort.None, ReviewReasoningEffort.High },
            passes.Select(pass => pass.ReasoningEffort).ToArray());
    }

    [Fact]
    public async Task BaselineReasoningEffort_PatchPersists_AndRegistryEchoesIt()
    {
        var client = await this.SeedClientAsync();
        var adminService = new ClientAdminService(this._dbContext, new UnlimitedStockQuotaGate());

        // Default before any opt-in is None (byte-identical to today: no effort sent).
        var initial = await this._registry.GetBaselineReasoningEffortAsync(client.Id, CancellationToken.None);
        Assert.Equal(ReviewReasoningEffort.None, initial);

        var updated = await adminService.PatchAsync(
            client.Id,
            null,
            null,
            baselineReasoningEffort: ReviewReasoningEffort.Medium,
            ct: CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(ReviewReasoningEffort.Medium, updated!.BaselineReasoningEffort);

        var persisted = await this._registry.GetBaselineReasoningEffortAsync(client.Id, CancellationToken.None);
        Assert.Equal(ReviewReasoningEffort.Medium, persisted);
    }

    [Fact]
    public async Task ReviewPasses_PatchReplacesListWholesale()
    {
        var client = await this.SeedClientAsync();
        var adminService = new ClientAdminService(this._dbContext, new UnlimitedStockQuotaGate());
        var original = await this.SeedChatModelAsync(client.Id);
        var replacement = await this.SeedChatModelAsync(client.Id);

        await adminService.PatchAsync(
            client.Id,
            null,
            null,
            reviewPasses: new List<ReviewPassDto> { new(0, original) },
            ct: CancellationToken.None);

        await adminService.PatchAsync(
            client.Id,
            null,
            null,
            reviewPasses: new List<ReviewPassDto> { new(0, replacement) },
            ct: CancellationToken.None);

        var passes = await this._registry.GetReviewPassesAsync(client.Id, CancellationToken.None);
        Assert.Equal(new[] { replacement }, passes.Select(pass => pass.ConfiguredModelId).ToArray());
    }

    [Fact]
    public async Task DefaultReviewPipelineProfileId_RoundTripsNullableValueAcrossPersistence()
    {
        var client = await this.SeedClientAsync();
        var adminService = new ClientAdminService(this._dbContext, new UnlimitedStockQuotaGate());

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

    [Fact]
    public async Task GetOutputLanguageAsync_NewClient_ReturnsTheDefault()
    {
        var client = await this.SeedClientAsync();

        var language = await this._registry.GetOutputLanguageAsync(client.Id);

        Assert.Equal(ReviewOutputLanguage.Default, language);
    }

    [Fact]
    public async Task GetOutputLanguageAsync_ConfiguredClient_ReturnsTheStoredTag()
    {
        var client = await this.SeedClientAsync();
        client.OutputLanguage = "de";
        await this._dbContext.SaveChangesAsync();

        var language = await this._registry.GetOutputLanguageAsync(client.Id);

        Assert.Equal("de", language);
    }

    [Fact]
    public async Task GetOutputLanguageAsync_UnknownClient_ReturnsTheDefault()
    {
        var language = await this._registry.GetOutputLanguageAsync(Guid.NewGuid());

        Assert.Equal(ReviewOutputLanguage.Default, language);
    }

    [Fact]
    public async Task GetReviewAdmissionPolicyAsync_ReturnsTheStoredBoundsAndLeavesAnUnsetOneNull()
    {
        var client = await this.SeedClientAsync();
        client.AdmissionMaxChangedFiles = 150;
        client.AdmissionMaxChangedLines = 20_000;
        client.AdmissionMaxReviewsPerPullRequestPerHour = 4;
        await this._dbContext.SaveChangesAsync();

        var policy = await this._registry.GetReviewAdmissionPolicyAsync(client.Id);

        Assert.Equal(150, policy.MaxChangedFiles);
        Assert.Equal(20_000, policy.MaxChangedLines);
        Assert.Equal(4, policy.MaxReviewsPerPullRequestPerHour);
        Assert.Null(policy.MaxDiffBytes);
        Assert.Null(policy.MaxRepositoryMegabytes);
        Assert.True(policy.AnyConfigured);
    }

    [Fact]
    public async Task GetReviewAdmissionPolicyAsync_ReturnsAnUnboundedPolicyForAnUnknownClient()
    {
        var policy = await this._registry.GetReviewAdmissionPolicyAsync(Guid.NewGuid());

        Assert.False(policy.AnyConfigured);
        Assert.Null(policy.MaxChangedFiles);
    }

    [Fact]
    public async Task GetTenantReviewLimitsAsync_ReturnsNothingForATenantThatStatesNoLimits()
    {
        var client = await this.SeedClientAsync();

        var limits = await this._registry.GetTenantReviewLimitsAsync(client.Id);

        Assert.False(limits.AnyStated);
        Assert.Null(limits.MaxFileSizeBytes);
        Assert.Null(limits.MaxStructuralParseBytes);
    }

    [Fact]
    public async Task GetTenantReviewLimitsAsync_ReturnsNothingForAnUnknownClient()
    {
        var limits = await this._registry.GetTenantReviewLimitsAsync(Guid.NewGuid());

        Assert.False(limits.AnyStated);
        Assert.Null(limits.MaxFileSizeBytes);
        Assert.Null(limits.MaxStructuralParseBytes);
    }

    [Fact]
    public async Task GetTenantReviewLimitsAsync_ReturnsTheLimitsOfTheTenantTheClientBelongsTo()
    {
        var tenantId = Guid.NewGuid();
        this._dbContext.Tenants.Add(
            new TenantRecord
            {
                Id = tenantId,
                Slug = $"limits-{tenantId:N}",
                DisplayName = "Limits Tenant",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                AiMaxFileSizeBytes = 262_144,
                AiMaxStructuralParseBytes = 131_072,
            });
        var client = new ClientRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DisplayName = "Limits Client",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        this._dbContext.Clients.Add(client);
        await this._dbContext.SaveChangesAsync();
        this._seededClientIds.Add(client.Id);

        try
        {
            var limits = await this._registry.GetTenantReviewLimitsAsync(client.Id);

            Assert.Equal(262_144, limits.MaxFileSizeBytes);
            Assert.Equal(131_072, limits.MaxStructuralParseBytes);
        }
        finally
        {
            await this._dbContext.Clients.Where(row => row.Id == client.Id).ExecuteDeleteAsync();
            await this._dbContext.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
            this._seededClientIds.Remove(client.Id);

            // The bulk delete went straight to the database, so the context still tracks a row that is gone.
            // Left tracked, the next save through this context works on a stale entity.
            this._dbContext.Entry(client).State = EntityState.Detached;
        }
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

    // Seeds one chat-capable configured model (on its own connection profile) for the client and returns its id,
    // so a review-pass entry can satisfy the configured-model foreign key. Cleaned up by the client-delete cascade.
    private async Task<Guid> SeedChatModelAsync(Guid clientId)
    {
        var profileId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        this._dbContext.AiConnectionProfiles.Add(
            new AiConnectionProfileRecord
            {
                Id = profileId,
                ClientId = clientId,
                DisplayName = $"Connection {profileId:N}",
                ProviderKind = "AzureOpenAi",
                BaseUrl = "https://x.openai.azure.com/",
                AuthMode = "AzureIdentity",
                DiscoveryMode = AiDiscoveryMode.ManualOnly.ToString(),
                DefaultHeaders = [],
                DefaultQueryParams = [],
                IsActive = false,
                CreatedAt = now,
                UpdatedAt = now,
                PurposeBindings = [],
                ConfiguredModels =
                [
                    new AiConfiguredModelRecord
                    {
                        Id = modelId,
                        ConnectionProfileId = profileId,
                        RemoteModelId = $"gpt-4o-{modelId:N}",
                        DisplayName = "gpt-4o",
                        OperationKinds = [AiOperationKind.Chat.ToString()],
                        SupportedProtocolModes = [ProviderDeclaredProtocolModes.Auto.ToString()],
                        SupportsStructuredOutput = true,
                        SupportsToolUse = true,
                        Source = AiConfiguredModelSource.Manual.ToString(),
                    },
                ],
            });
        await this._dbContext.SaveChangesAsync();
        return modelId;
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
