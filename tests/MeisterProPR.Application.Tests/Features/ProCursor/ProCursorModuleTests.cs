// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
using MeisterProPR.ProCursor.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Application.Tests.Features.ProCursor;

public sealed class ProCursorModuleTests
{
    [Fact]
    public void AddProCursorModule_BindsOptionsAndRegistersGatewayContract()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddProCursorModule(BuildConfiguration(withDatabaseConnectionString: false, stubMode: true));

        Assert.NotNull(FindService<IProCursorGateway>(services));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ProCursorOptions>>().Value;
        Assert.Equal(7, options.MaxIndexConcurrency);
        Assert.Equal(20, options.MaxQueryResults);
        Assert.NotNull(provider.GetRequiredService<IProCursorTrackedBranchChangeDetector>());
    }

    [Fact]
    public void AddProCursorModule_WhenDatabaseConnectionStringIsConfigured_RegistersPersistenceContracts()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddProCursorModule(BuildConfiguration(withDatabaseConnectionString: true, stubMode: true));

        Assert.NotNull(FindService<IProCursorKnowledgeSourceRepository>(services));
        Assert.NotNull(FindService<IProCursorIndexJobRepository>(services));
        Assert.NotNull(FindService<IProCursorIndexSnapshotRepository>(services));
        Assert.NotNull(FindService<IProCursorSymbolGraphRepository>(services));
    }

    [Fact]
    public void ProCursorTokenUsageCaptureRequest_PreservesSafeMetadataContract()
    {
        var clientId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var request = new ProCursorTokenUsageCaptureRequest(
            clientId,
            sourceId,
            "Platform Wiki",
            "req-1",
            new DateTimeOffset(2026, 4, 4, 8, 0, 0, TimeSpan.Zero),
            ProCursorTokenUsageCallType.Embedding,
            "text-embedding-3-small",
            "text-embedding-3-small",
            "cl100k_base",
            120,
            0,
            120,
            false,
            0.00012m,
            false,
            ResourceId: "ado://wiki/platform",
            SourcePath: "/docs/platform.md",
            SafeMetadataJson: "{\"traceId\":\"abc\"}");

        Assert.Equal(clientId, request.ClientId);
        Assert.Equal(sourceId, request.ProCursorSourceId);
        Assert.Equal("ado://wiki/platform", request.ResourceId);
        Assert.Equal("/docs/platform.md", request.SourcePath);
        Assert.Equal("{\"traceId\":\"abc\"}", request.SafeMetadataJson);
    }

    private static IConfiguration BuildConfiguration(bool withDatabaseConnectionString, bool stubMode)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_CONNECTION_STRING"] = withDatabaseConnectionString ? "Host=localhost;Database=meister;Username=test;Password=test" : null,
                ["ADO_STUB_PR"] = stubMode ? "true" : "false",
                ["PROCURSOR_MAX_INDEX_CONCURRENCY"] = "7",
                ["PROCURSOR_MAX_QUERY_RESULTS"] = "20",
                ["PROCURSOR_MAX_SOURCES_PER_QUERY"] = "5",
                ["PROCURSOR_CHUNK_TARGET_LINES"] = "120",
                ["PROCURSOR_MINI_INDEX_TTL_MINUTES"] = "30",
                ["PROCURSOR_REFRESH_POLL_SECONDS"] = "15",
                ["PROCURSOR_TEMP_WORKSPACE_RETENTION_MINUTES"] = "60",
                ["PROCURSOR_EMBEDDING_DIMENSIONS"] = "1536",
                ["PROCURSOR_TOKEN_USAGE_ROLLUP_POLL_SECONDS"] = "30",
                ["PROCURSOR_TOKEN_USAGE_EVENT_RETENTION_DAYS"] = "30",
                ["PROCURSOR_TOKEN_USAGE_ROLLUP_RETENTION_DAYS"] = "90",
            })
            .Build();
    }

    private static ServiceDescriptor? FindService<TService>(IServiceCollection services)
    {
        return services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(TService));
    }
}
