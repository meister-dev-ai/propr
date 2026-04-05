// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.AzureDevOps.ProCursor;
using Microsoft.TeamFoundation.Wiki.WebApi;

namespace MeisterProPR.Infrastructure.Tests.AzureDevOps.ProCursor;

public sealed class AdoWikiRepositoryResolverTests
{
    private static readonly Guid ClientId = Guid.Parse("20000000-0000-0000-0000-000000000001");

    [Fact]
    public void ResolveRepositoryId_UsesCanonicalWikiIdentifierWhenPresent()
    {
        var source = CreateWikiSource();
        var wikiId = Guid.Parse("30000000-0000-0000-0000-000000000071");
        var repositoryId = Guid.Parse("30000000-0000-0000-0000-000000000072");

        source.UpdateDefinition(
            source.DisplayName,
            source.OrganizationUrl,
            source.ProjectId,
            "legacy-repository-id",
            source.DefaultBranch,
            source.RootPath,
            source.IsEnabled,
            source.SymbolMode,
            source.OrganizationScopeId,
            "azureDevOps",
            wikiId.ToString(),
            "Engineering Wiki");

        var resolvedRepositoryId = AdoWikiRepositoryResolver.ResolveRepositoryId(
            source,
            [new WikiV2 { Id = wikiId, RepositoryId = repositoryId }]);

        Assert.Equal(repositoryId.ToString(), resolvedRepositoryId);
    }

    [Fact]
    public void ResolveRepositoryId_UsesLegacyDisplayNameWhenCanonicalMetadataIsMissing()
    {
        var source = new ProCursorKnowledgeSource(
            Guid.NewGuid(),
            ClientId,
            "Meister DEV Wiki",
            ProCursorSourceKind.AdoWiki,
            "https://dev.azure.com/test-org",
            "test-project",
            "2",
            "main",
            "/wiki",
            true,
            "auto");

        source.AddTrackedBranch(Guid.NewGuid(), "main", ProCursorRefreshTriggerMode.BranchUpdate, true);

        var repositoryId = Guid.Parse("30000000-0000-0000-0000-000000000073");

        var resolvedRepositoryId = AdoWikiRepositoryResolver.ResolveRepositoryId(
            source,
            [new WikiV2
            {
                Id = Guid.Parse("30000000-0000-0000-0000-000000000074"),
                Name = "Meister DEV Wiki",
                RepositoryId = repositoryId,
            }]);

        Assert.Equal(repositoryId.ToString(), resolvedRepositoryId);
    }

    [Fact]
    public void ResolveRepositoryId_UsesOnlyResolvableWikiForLegacyManualSources()
    {
        var source = new ProCursorKnowledgeSource(
            Guid.NewGuid(),
            ClientId,
            "Legacy docs label",
            ProCursorSourceKind.AdoWiki,
            "https://dev.azure.com/test-org",
            "test-project",
            "2",
            "main",
            "/wiki",
            true,
            "auto");

        source.AddTrackedBranch(Guid.NewGuid(), "main", ProCursorRefreshTriggerMode.BranchUpdate, true);

        var repositoryId = Guid.Parse("30000000-0000-0000-0000-000000000075");

        var resolvedRepositoryId = AdoWikiRepositoryResolver.ResolveRepositoryId(
            source,
            [new WikiV2
            {
                Id = Guid.Parse("30000000-0000-0000-0000-000000000076"),
                Name = "Project Wiki",
                RepositoryId = repositoryId,
            }]);

        Assert.Equal(repositoryId.ToString(), resolvedRepositoryId);
    }

    [Fact]
    public void ResolveRepositoryId_ThrowsWhenWikiCannotBeResolved()
    {
        var source = CreateWikiSource();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AdoWikiRepositoryResolver.ResolveRepositoryId(source, []));

        Assert.Contains("Unable to resolve the backing repository", exception.Message, StringComparison.Ordinal);
    }

    private static ProCursorKnowledgeSource CreateWikiSource()
    {
        var source = new ProCursorKnowledgeSource(
            Guid.NewGuid(),
            ClientId,
            "Wiki",
            ProCursorSourceKind.AdoWiki,
            "https://dev.azure.com/test-org",
            "test-project",
            "wiki-id",
            "main",
            "/wiki",
            true,
            "auto");

        source.AddTrackedBranch(Guid.NewGuid(), "main", ProCursorRefreshTriggerMode.BranchUpdate, true);
        return source;
    }
}
