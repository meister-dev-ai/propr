using MeisterProPR.Application.DTOs;

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
            DateTimeOffset.UtcNow);

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
            DateTimeOffset.UtcNow);

        Assert.Null(dto.ReviewerId);
    }
}
