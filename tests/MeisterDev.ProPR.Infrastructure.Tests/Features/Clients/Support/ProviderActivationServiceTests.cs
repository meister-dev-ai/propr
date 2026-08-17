// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Clients.Support;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.Clients.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
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
            new StaticProviderReadinessProfileCatalog());

        var tasks = Enumerable.Range(0, 6)
            .Select(_ => sut.IsEnabledAsync(ScmProvider.GitHub, CancellationToken.None));

        var results = await Task.WhenAll(tasks);

        Assert.All(results, Assert.True);
    }

    private sealed class TestScmProviderRegistry : IScmProviderRegistry
    {
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

        public ICodeReviewPublicationService GetCodeReviewPublicationService(ScmProvider provider)
        {
            throw new NotSupportedException();
        }

        public IReviewDiscoveryProvider GetReviewDiscoveryProvider(ScmProvider provider)
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
