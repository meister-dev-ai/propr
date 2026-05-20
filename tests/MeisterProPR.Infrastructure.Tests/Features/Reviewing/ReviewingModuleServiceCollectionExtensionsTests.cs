// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.AI;
using MeisterProPR.Infrastructure.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Reviewing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
