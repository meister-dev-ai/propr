// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Clients.Support;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.Clients;
using MeisterDev.ProPR.Infrastructure.Features.Clients.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Clients.Support;

public sealed class ProviderActivationServiceTests
{
    [Fact]
    public async Task IsEnabledAsync_WithDbContextFactory_SupportsParallelReads()
    {
        var dbRoot = new InMemoryDatabaseRoot();
        var dbName = $"ProviderActivationServiceTests-{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase(dbName, dbRoot)
            .Options;

        await using var db = new MeisterProPRDbContext(options);
        db.ProviderActivations.AddRange(
            new ProviderActivationRecord
            {
                Provider = ScmProvider.GitHub,
                IsEnabled = true,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            new ProviderActivationRecord
            {
                Provider = ScmProvider.GitLab,
                IsEnabled = true,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();

        var factory = new PooledDbContextFactory<MeisterProPRDbContext>(options);
        var services = new ServiceCollection();
        services.AddSingleton<IScmProviderRegistry, TestScmProviderRegistry>();
        var serviceProvider = services.BuildServiceProvider();

        var sut = new ProviderActivationService(
            db,
            factory,
            serviceProvider,
            new StaticProviderReadinessProfileCatalog(MeisterDev.ProPR.TestSupport.LocalScmPolicies.ConfigurationPolicies));

        var tasks = Enumerable.Range(0, 6)
            .Select(_ => sut.IsEnabledAsync(ScmProvider.GitHub, CancellationToken.None));

        var results = await Task.WhenAll(tasks);

        Assert.All(results, Assert.True);
    }

    // The installation setting replaces the built-in default of the provider families without a stored activation,
    // and an activation that an administrator stored still decides.
    [Fact]
    public async Task TheInstallationSettingDecidesForProviderFamiliesWithoutAStoredActivation()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"ProviderActivationDefaults-{Guid.NewGuid():N}", new InMemoryDatabaseRoot())
            .Options;
        await using var db = new MeisterProPRDbContext(options);
        db.ProviderActivations.Add(new ProviderActivationRecord { Provider = ScmProvider.Forgejo, IsEnabled = true, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var services = new ServiceCollection();
        services.AddSingleton<IScmProviderRegistry, TestScmProviderRegistry>();
        await using var serviceProvider = services.BuildServiceProvider();
        var sut = new ProviderActivationService(
            db,
            new PooledDbContextFactory<MeisterProPRDbContext>(options),
            serviceProvider,
            new StaticProviderReadinessProfileCatalog(MeisterDev.ProPR.TestSupport.LocalScmPolicies.ConfigurationPolicies),
            Microsoft.Extensions.Options.Options.Create(
                new ProviderActivationOptions { DefaultEnabled = new HashSet<ScmProvider> { ScmProvider.AzureDevOps, ScmProvider.GitHub } }));

        var enabled = await sut.GetEnabledProvidersAsync(CancellationToken.None);

        Assert.Equal(
            new HashSet<ScmProvider> { ScmProvider.AzureDevOps, ScmProvider.GitHub, ScmProvider.Forgejo },
            enabled.ToHashSet());
    }

    [Theory]
    [InlineData("azureDevOps, GitHub", new[] { ScmProvider.AzureDevOps, ScmProvider.GitHub })]
    [InlineData("  ", null)]
    public void TheInstallationSettingIsReadWithoutRegardToCase(string setting, ScmProvider[]? expected)
    {
        using var provider = ClientsModule(setting);

        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProviderActivationOptions>>().Value;

        Assert.Equal(expected?.ToHashSet(), options.DefaultEnabled?.ToHashSet());
    }

    [Theory]
    [InlineData("github,bitbucket")]
    [InlineData("1")]
    [InlineData(" , ,")]
    public void AnUnknownOrMissingProviderFamilyInTheInstallationSettingStopsTheStart(string setting)
    {
        using var provider = ClientsModule(setting);

        var failure = Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() =>
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProviderActivationOptions>>().Value);
        Assert.Contains(ProviderActivationOptions.DefaultEnabledKey, failure.Message);
    }

    private static ServiceProvider ClientsModule(string setting)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [ProviderActivationOptions.DefaultEnabledKey] = setting })
            .Build();
        return new ServiceCollection().AddLogging()
            .AddClientsModule(configuration).BuildServiceProvider();
    }

    private sealed class TestScmProviderRegistry : IScmProviderRegistry
    {
        public IScmProviderCompatibilityCodec CompatibilityCodec => throw new NotSupportedException();
        public IScmIdentityPolicy GetIdentityPolicy(ScmProvider provider) => throw new NotSupportedException();
        public IWebhookIngressPolicy GetWebhookIngressPolicy(ScmProvider provider) => throw new NotSupportedException();
        public ICodeReviewPreparationPolicy GetCodeReviewPreparationPolicy(ScmProvider provider) => throw new NotSupportedException();
        public IReviewSourcePolicy GetSourceIdentityPolicy(ScmProvider provider) => throw new NotSupportedException();
        public IScmConnectionConfigurationPolicy GetConnectionConfigurationPolicy(ScmProvider provider) => throw new NotSupportedException();

        public bool IsRegistered(ScmProvider provider)
        {
            return true;
        }

        public IReadOnlyList<string> GetRegisteredCapabilities(ScmProvider provider)
        {
            return [];
        }

        public IRepositoryDiscoveryProvider GetRepositoryDiscoveryProvider(ScmProvider provider)
        {
            throw new NotSupportedException();
        }

        public bool SupportsActivePullRequestDiscovery(ScmProvider provider)
        {
            return false;
        }

        public bool SupportsReviewThreadReply(ScmProvider provider)
        {
            return false;
        }

        public bool RequiresReviewThreadIdentifier(ScmProvider provider)
        {
            return true;
        }


        public ICodeReviewQueryService GetCodeReviewQueryService(ScmProvider provider)
        {
            throw new NotSupportedException();
        }

        public ILatestRevisionReader GetLatestRevisionReader(ScmProvider provider)
        {
            throw new NotSupportedException();
        }

        public ICodeReviewPublicationService GetCodeReviewPublicationService(ScmProvider provider)
        {
            throw new NotSupportedException();
        }

        public IReviewDiscoveryProvider GetReviewDiscoveryProvider(ScmProvider provider)
        {
            throw new NotSupportedException();
        }

        public IReviewSourcePolicy GetReviewSourcePolicy(ScmProvider provider) => throw new InvalidOperationException();


        public IReviewOverviewProvider GetReviewOverviewProvider(ScmProvider provider)
        {
            throw new NotSupportedException();
        }

        public IReviewerIdentityService GetReviewerIdentityService(ScmProvider provider)
        {
            throw new NotSupportedException();
        }

        public IReviewAssignmentService GetReviewAssignmentService(ScmProvider provider)
        {
            throw new NotSupportedException();
        }

        public IReviewThreadStatusWriter GetReviewThreadStatusWriter(ScmProvider provider)
        {
            throw new NotSupportedException();
        }

        public IReviewThreadReplyPublisher GetReviewThreadReplyPublisher(ScmProvider provider)
        {
            throw new NotSupportedException();
        }

        public IProviderAdminDiscoveryService GetProviderAdminDiscoveryService(ScmProvider provider)
        {
            throw new NotSupportedException();
        }

        public IWebhookIngressService GetWebhookIngressService(ScmProvider provider)
        {
            throw new NotSupportedException();
        }

        public ILinkedItemProvider GetLinkedItemProvider(ScmProvider provider)
        {
            throw new NotSupportedException();
        }
    }
}
