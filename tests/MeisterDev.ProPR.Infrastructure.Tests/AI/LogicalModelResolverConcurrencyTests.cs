// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;
using MeisterDev.ProPR.Infrastructure.Tests.Repositories;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

/// <summary>
///     Runs <see cref="LogicalModelResolver" /> concurrently over the real catalog repository and client registry,
///     both holding the same context, the way one review job's dependency-injection scope supplies them to the
///     per-file review loop.
/// </summary>
[Collection("PostgresIntegration2")]
public sealed class LogicalModelResolverConcurrencyTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private const string RoleName = "deep";

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly Guid _configuredModelId = Guid.NewGuid();
    private DbContextOptions<MeisterProPRDbContext> _options = null!;
    private MeisterProPRDbContext _scopedContext = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        this._options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._scopedContext = new MeisterProPRDbContext(this._options);

        var now = DateTimeOffset.UtcNow;
        this._scopedContext.Tenants.Add(
            new TenantRecord
            {
                Id = this._tenantId,
                Slug = "lm-concurrency-" + this._tenantId.ToString("N"),
                DisplayName = "Logical Model Concurrency Tenant",
                IsActive = true,
                LocalLoginEnabled = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
        this._scopedContext.Clients.Add(
            new ClientRecord
            {
                Id = this._clientId,
                TenantId = this._tenantId,
                DisplayName = "Logical Model Concurrency Client",
                IsActive = true,
                CreatedAt = now,
            });
        await this._scopedContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (this._scopedContext is null)
        {
            return;
        }

        await this._scopedContext.LogicalModels.Where(x => x.TenantId == this._tenantId).ExecuteDeleteAsync();
        await this._scopedContext.Clients.Where(c => c.Id == this._clientId).ExecuteDeleteAsync();
        await this._scopedContext.Tenants.Where(t => t.Id == this._tenantId).ExecuteDeleteAsync();
        await this._scopedContext.DisposeAsync();
    }

    // A tenant-catalog role makes each resolution read the client overrides, the tenant entries and the client's
    // tenant, which covers every database read on the resolution path.
    [Fact]
    public async Task ResolveChatRuntimeAsync_ServesConcurrentResolutionsThroughOneScopedContext()
    {
        var model = AiConnectionTestFactory.CreateChatModel("deep-model", this._configuredModelId);
        var connection = AiConnectionTestFactory.CreateConnection(this._clientId, [model]);
        await this.SeedTenantEntryAsync(connection.Id);

        var resolver = this.CreateResolver(connection);

        var resolutions = Enumerable.Range(0, 24)
            .Select(_ => Task.Run(() => resolver.ResolveChatRuntimeAsync(this._clientId, RoleName)))
            .ToList();

        var results = await Task.WhenAll(resolutions);

        Assert.All(
            results, result =>
            {
                Assert.Equal(RoleName, result.RoleName);
                Assert.Equal(LogicalModelLayer.TenantCatalog, result.Layer);
            });
    }

    private async Task SeedTenantEntryAsync(Guid connectionId)
    {
        var now = DateTimeOffset.UtcNow;
        this._scopedContext.LogicalModels.Add(
            new LogicalModelRecord
            {
                Id = Guid.NewGuid(),
                TenantId = this._tenantId,
                Name = RoleName,
                Capability = AiOperationKind.Chat,
                ConnectionId = connectionId,
                ConfiguredModelId = this._configuredModelId,
                ReasoningEffort = ReviewReasoningEffort.High,
                ProtocolMode = ProviderDeclaredProtocolModes.Auto,
                CreatedAt = now,
                UpdatedAt = now,
            });
        await this._scopedContext.SaveChangesAsync();
    }

    private LogicalModelResolver CreateResolver(AiConnectionDto connection)
    {
        var contextFactory = new TestDbContextFactory(this._options);

        // The connection repository and the scope guard read through their own contexts or not at all, and the
        // runtime factory builds no database state, so they are substituted.
        var connections = Substitute.For<IAiConnectionRepository>();
        connections.GetByIdAsync(connection.Id, Arg.Any<CancellationToken>()).Returns(connection);
        var scopeGuard = Substitute.For<IAiConnectionScopeGuard>();
        scopeGuard.ValidateAsync(Arg.Any<AiConnectionDto>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        var runtimeFactory = Substitute.For<IAiRuntimeFactory>();
        runtimeFactory
            .CreateChatRuntime(Arg.Any<AiConnectionDto>(), Arg.Any<AiConfiguredModelDto>(), Arg.Any<AiPurposeBindingDto>(), Arg.Any<string?>())
            .Returns(_ => Substitute.For<IResolvedAiChatRuntime>());

        var catalog = new LogicalModelCatalogRepository(
            this._scopedContext,
            Substitute.For<ILogicalModelCapabilityValidator>(),
            connections,
            scopeGuard,
            DeclaringProviderFamilies.None(),
            contextFactory);
        var clients = new DbClientRegistry(
            this._scopedContext,
            Substitute.For<IClientScmConnectionRepository>(),
            Substitute.For<IClientReviewerIdentityRepository>(),
            contextFactory: contextFactory);

        return new LogicalModelResolver(catalog, connections, runtimeFactory, clients, scopeGuard);
    }

    // Supplies an independent context per call, the way dependency injection supplies one at run time.
    private sealed class TestDbContextFactory(DbContextOptions<MeisterProPRDbContext> options)
        : IDbContextFactory<MeisterProPRDbContext>
    {
        public MeisterProPRDbContext CreateDbContext() => new(options);
    }
}
