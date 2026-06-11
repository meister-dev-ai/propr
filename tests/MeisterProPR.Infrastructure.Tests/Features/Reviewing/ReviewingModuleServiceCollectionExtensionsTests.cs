// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Options;
using MeisterProPR.Infrastructure.AI;
using MeisterProPR.Infrastructure.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Reviewing;
using MeisterProPR.Infrastructure.Features.Reviewing.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Reviewing.Workspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing;

public sealed class ReviewingModuleServiceCollectionExtensionsTests
{
    [Fact]
    public void AddReviewingModule_RegistersPromptTemplateInfrastructure()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration();

        services.AddInfrastructureSupport(configuration);
        services.AddReviewingModule(configuration);

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<PromptTemplateFileProvider>());
        Assert.NotNull(provider.GetRequiredService<PromptTemplatePartialRegistry>());
        Assert.NotNull(provider.GetRequiredService<HandlebarsPromptRenderer>());
    }

    [Fact]
    public void AddReviewingModule_UsesApplicationBaseDirectoryForPromptTemplateRootWhenNoHostEnvironmentProvided()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration();

        services.AddInfrastructureSupport(configuration);
        services.AddReviewingModule(configuration);

        using var provider = services.BuildServiceProvider();
        var fileProvider = provider.GetRequiredService<PromptTemplateFileProvider>();

        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, PromptTemplateFileProvider.PromptRootRelativePath),
            fileProvider.PromptRootPath);
    }

    [Fact]
    public void AddReviewWorkspaceServices_RegistersServicesAndBindsOptions()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ADO_SKIP_TOKEN_VALIDATION"] = "true",
                    ["ADO_STUB_PR"] = "true",
                    ["MEISTER_JWT_SECRET"] = "test-reviewing-module-jwt-secret-32!",
                    ["REVIEW_WORKSPACE_ROOT_PATH"] = "/tmp/review-workspaces",
                    ["REVIEW_WORKSPACE_RETENTION_MINUTES"] = "240",
                    ["REVIEW_WORKSPACE_MAX_CACHE_SIZE_MEGABYTES"] = "2048",
                    ["REVIEW_WORKSPACE_MAX_CONCURRENT_PREPARATIONS"] = "8",
                    ["REVIEW_WORKSPACE_FETCH_DEPTH_POLICY"] = "full",
                })
            .Build();

        services.AddInfrastructureSupport(configuration);
        services.AddReviewWorkspaceServices(configuration);

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IReviewRepositoryWorkspaceManager>());
        Assert.NotNull(provider.GetRequiredService<IReviewWorkspaceRemoteResolver>());
        Assert.NotNull(provider.GetRequiredService<GitCommandRunner>());
        Assert.NotNull(provider.GetRequiredService<ReviewWorkspaceCleanupService>());

        var options = provider.GetRequiredService<IOptions<ReviewWorkspaceOptions>>().Value;
        Assert.Equal("/tmp/review-workspaces", options.RootPath);
        Assert.Equal(240, options.RetentionMinutes);
        Assert.Equal(2048, options.MaxCacheSizeMegabytes);
        Assert.Equal(8, options.MaxConcurrentPreparations);
        Assert.Equal("full", options.FetchDepthPolicy);
    }

    private static IConfiguration CreateConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ADO_SKIP_TOKEN_VALIDATION"] = "true",
                    ["ADO_STUB_PR"] = "true",
                    ["MEISTER_JWT_SECRET"] = "test-reviewing-module-jwt-secret-32!",
                })
            .Build();
    }
}
