// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.ProCursor.Persistence;
using MeisterProPR.ProCursor.Service.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace MeisterProPR.ProCursor.Service.Tests.Support;

public sealed class ProCursorServiceFactory : WebApplicationFactory<Program>
{
    public const string SharedKey = "test-procursor-shared-key";

    public IProCursorGateway Gateway { get; } = Substitute.For<IProCursorGateway>();
    public IProCursorScmBroker ScmBroker { get; } = Substitute.For<IProCursorScmBroker>();
    public IProCursorEmbeddingBroker EmbeddingBroker { get; } = Substitute.For<IProCursorEmbeddingBroker>();
    public IProCursorTokenUsageReadRepository TokenUsageReadRepository { get; } = Substitute.For<IProCursorTokenUsageReadRepository>();
    public IProCursorTokenUsageRebuildService TokenUsageRebuildService { get; } = Substitute.For<IProCursorTokenUsageRebuildService>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("PROCURSOR_PROPR_BASE_URL", "http://propr.internal:8080");
        builder.UseSetting("PROCURSOR_SHARED_KEY", SharedKey);
        builder.UseSetting("PROCURSOR_DB_CONNECTION_STRING", "Host=localhost;Database=procursor_test;Username=test;Password=test");
        builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
        builder.UseSetting("ADO_STUB_PR", "true");
        builder.UseSetting("MEISTER_JWT_SECRET", "test-procursor-service-jwt-secret-32!");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IProCursorGateway>();
            services.RemoveAll<IProCursorScmBroker>();
            services.RemoveAll<IProCursorEmbeddingBroker>();
            services.RemoveAll<IProCursorTokenUsageReadRepository>();
            services.RemoveAll<IProCursorTokenUsageRebuildService>();
            ConfigureInMemoryOperationalDb(services);
            services.AddSingleton(this.Gateway);
            services.AddSingleton(this.ScmBroker);
            services.AddSingleton(this.EmbeddingBroker);
            services.AddSingleton(this.TokenUsageReadRepository);
            services.AddSingleton(this.TokenUsageRebuildService);
        });
    }

    internal static void ConfigureInMemoryOperationalDb(IServiceCollection services)
    {
        services.RemoveAll<ProCursorOperationalDbContext>();
        services.RemoveAll<DbContextOptions<ProCursorOperationalDbContext>>();
        services.RemoveAll<Microsoft.EntityFrameworkCore.IDbContextFactory<ProCursorOperationalDbContext>>();

        var options = new DbContextOptionsBuilder<ProCursorOperationalDbContext>()
            .UseInMemoryDatabase($"procursor-service-{Guid.NewGuid():N}")
            .Options;

        services.AddSingleton(options);
        services.AddScoped(_ => new ProCursorOperationalDbContext(options));
        services.AddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<ProCursorOperationalDbContext>>(
            _ => new TestDbContextFactory(options));
    }

    private sealed class TestDbContextFactory(DbContextOptions<ProCursorOperationalDbContext> options)
        : Microsoft.EntityFrameworkCore.IDbContextFactory<ProCursorOperationalDbContext>
    {
        public ProCursorOperationalDbContext CreateDbContext()
        {
            return new ProCursorOperationalDbContext(options);
        }

        public Task<ProCursorOperationalDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ProCursorOperationalDbContext(options));
        }
    }
}
