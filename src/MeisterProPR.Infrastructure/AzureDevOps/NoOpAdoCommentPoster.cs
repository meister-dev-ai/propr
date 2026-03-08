using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.AzureDevOps;

/// <summary>
///     A no-op implementation of <see cref="IAdoCommentPoster" /> for local development.
///     Logs the review result instead of posting it to Azure DevOps.
///     Enable by setting ADO_STUB_PR=true in user secrets / environment variables.
/// </summary>
public sealed class NoOpAdoCommentPoster(ILogger<NoOpAdoCommentPoster> logger) : IAdoCommentPoster
{
    public Task PostAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        ReviewResult result,
        CancellationToken cancellationToken = default)
    {
        logger.LogWarning(
            "ADO_STUB_PR is enabled -- skipping comment post for PR#{PrId}. Review summary: {Summary}",
            pullRequestId,
            result.Summary);
        foreach (var comment in result.Comments)
        {
            logger.LogInformation(
                "[STUB COMMENT] {Severity} @ {File}:{Line} -- {Message}",
                comment.Severity,
                comment.FilePath,
                comment.LineNumber,
                comment.Message);
        }

        return Task.CompletedTask;
    }
}