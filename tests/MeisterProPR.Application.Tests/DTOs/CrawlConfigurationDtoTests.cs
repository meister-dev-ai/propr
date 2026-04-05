// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Tests.DTOs;

/// <summary>T003 — Confirm <see cref="CrawlConfigurationDto.ReviewerId" /> is nullable.</summary>
public sealed class CrawlConfigurationDtoTests
{
    [Fact]
    public void ReviewerId_CanBeNonNull()
    {
        var reviewerId = Guid.NewGuid();
        var dto = new CrawlConfigurationDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.com/org",
            "project",
            reviewerId,
            60,
            true,
            DateTimeOffset.UtcNow,
            []);

        Assert.Equal(reviewerId, dto.ReviewerId);
    }

    [Fact]
    public void ReviewerId_CanBeNull()
    {
        var dto = new CrawlConfigurationDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.com/org",
            "project",
            null, // ReviewerId is Guid? — must accept null
            60,
            true,
            DateTimeOffset.UtcNow,
            []);

        Assert.Null(dto.ReviewerId);
    }

    [Fact]
    public void ProCursorSourceScopeMode_DefaultsToAllClientSources()
    {
        var dto = new CrawlConfigurationDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.com/org",
            "project",
            null,
            60,
            true,
            DateTimeOffset.UtcNow,
            []);

        Assert.Equal(ProCursorSourceScopeMode.AllClientSources, dto.ProCursorSourceScopeMode);
        Assert.Null(dto.OrganizationScopeId);
    }

    [Fact]
    public void SourceScopeFields_CanBePopulated()
    {
        var organizationScopeId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var invalidSourceId = Guid.NewGuid();

        var dto = new CrawlConfigurationDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.com/org",
            "project",
            null,
            60,
            true,
            DateTimeOffset.UtcNow,
            [],
            organizationScopeId,
            ProCursorSourceScopeMode.SelectedSources,
            [sourceId],
            [invalidSourceId]);

        Assert.Equal(organizationScopeId, dto.OrganizationScopeId);
        Assert.Equal(ProCursorSourceScopeMode.SelectedSources, dto.ProCursorSourceScopeMode);
        Assert.Contains(sourceId, dto.ProCursorSourceIds!);
        Assert.Contains(invalidSourceId, dto.InvalidProCursorSourceIds!);
    }

    [Fact]
    public void RepoFilter_CanCaptureCanonicalSelectionMetadata()
    {
        var repoFilter = new CrawlRepoFilterDto(
            Guid.NewGuid(),
            "Repository One",
            ["main", "release/*"],
            new CanonicalSourceReferenceDto("azureDevOps", "repo-1"),
            "Repository One");

        var dto = new CrawlConfigurationDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.com/org",
            "project",
            null,
            60,
            true,
            DateTimeOffset.UtcNow,
            [repoFilter]);

        Assert.Single(dto.RepoFilters);
        Assert.Equal("Repository One", dto.RepoFilters[0].RepositoryName);
        Assert.Equal("Repository One", dto.RepoFilters[0].DisplayName);
        Assert.Equal("azureDevOps", dto.RepoFilters[0].CanonicalSourceRef!.Provider);
        Assert.Equal("repo-1", dto.RepoFilters[0].CanonicalSourceRef!.Value);
        Assert.Equal(["main", "release/*"], dto.RepoFilters[0].TargetBranchPatterns);
    }
}
