// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Azure.Core;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AzureDevOps;

public sealed class AdoCodeReviewPublicationServiceTests
{
    [Fact]
    public void ProviderAdapters_RegisterAzureDevOpsPublicationUnderNeutralInterface()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        services.AddSingleton(Substitute.For<IClientScmConnectionRepository>());
        services.AddSingleton(Substitute.For<IClientScmScopeRepository>());
        services.AddSingleton(Substitute.For<IAdoCommentPoster>());

        services.AddAzureDevOpsProviderAdapters();
        services.AddAzureDevOpsInfrastructureServices(configuration, Substitute.For<TokenCredential>());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var publicationService = scope.ServiceProvider
            .GetServices<ICodeReviewPublicationService>()
            .Single(service => service.Provider == ScmProvider.AzureDevOps);

        Assert.IsType<AdoCodeReviewPublicationService>(publicationService);
    }
}
