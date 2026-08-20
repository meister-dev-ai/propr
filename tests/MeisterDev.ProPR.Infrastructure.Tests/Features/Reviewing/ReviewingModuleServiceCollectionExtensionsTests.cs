// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.Infrastructure.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Workspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing;

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
        Assert.NotNull(provider.GetRequiredService<ReviewWorkspacePreparationThrottle>());
    }

    [Theory]
    [InlineData("blobless")]
    [InlineData("shallow")]
    [InlineData("FULL")]
    public void AddReviewWorkspaceServices_AcceptsEveryImplementedFetchDepthPolicy(string policy)
    {
        using var provider = BuildProviderWithFetchDepthPolicy(policy);

        Assert.Equal(policy, provider.GetRequiredService<IOptions<ReviewWorkspaceOptions>>().Value.FetchDepthPolicy);
    }

    /// <summary>
    ///     A policy name nothing implements used to bind and validate and then change nothing about the fetch,
    ///     so a setting that reads like a mitigation quietly did not apply. It is refused at startup instead.
    /// </summary>
    [Fact]
    public void AddReviewWorkspaceServices_RefusesAnUnimplementedFetchDepthPolicy()
    {
        using var provider = BuildProviderWithFetchDepthPolicy("treeless");

        var failure = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ReviewWorkspaceOptions>>().Value);
        Assert.Contains("FetchDepthPolicy", failure.Message, StringComparison.Ordinal);
        Assert.Contains("blobless", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The depth is read only under the shallow policy, so a value outside its range under one of the
    ///     others is not a setting anything acts on. Refusing startup for it stopped deployments that carry
    ///     one configuration across policies, and reported a range error against a setting documented as
    ///     ignored.
    /// </summary>
    [Theory]
    [InlineData("full")]
    [InlineData("blobless")]
    public void AddReviewWorkspaceServices_AcceptsADepthOutOfRangeUnderAPolicyThatIgnoresIt(string policy)
    {
        using var provider = BuildProviderWithFetchDepthPolicy(policy, "0");

        Assert.Equal(0, provider.GetRequiredService<IOptions<ReviewWorkspaceOptions>>().Value.FetchDepth);
    }

    [Fact]
    public void AddReviewWorkspaceServices_RefusesADepthOutOfRangeUnderTheShallowPolicy()
    {
        using var provider = BuildProviderWithFetchDepthPolicy("shallow", "0");

        var failure = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ReviewWorkspaceOptions>>().Value);
        Assert.Contains("FetchDepth", failure.Message, StringComparison.Ordinal);
        Assert.Contains("shallow", failure.Message, StringComparison.Ordinal);
    }

    private static ServiceProvider BuildProviderWithFetchDepthPolicy(string policy, string? fetchDepth = null)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ADO_SKIP_TOKEN_VALIDATION"] = "true",
                    ["ADO_STUB_PR"] = "true",
                    ["MEISTER_JWT_SECRET"] = "test-reviewing-module-jwt-secret-32!",
                    ["REVIEW_WORKSPACE_FETCH_DEPTH_POLICY"] = policy,
                    ["REVIEW_WORKSPACE_FETCH_DEPTH"] = fetchDepth,
                })
            .Build();

        services.AddInfrastructureSupport(configuration);
        services.AddReviewWorkspaceServices(configuration);
        return services.BuildServiceProvider();
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
