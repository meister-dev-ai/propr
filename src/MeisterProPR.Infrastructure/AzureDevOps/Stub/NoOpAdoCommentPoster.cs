// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.AzureDevOps.Stub;

/// <summary>
///     A no-op implementation of <see cref="IAdoCommentPoster" /> for local development.
///     Logs the review result instead of posting it to Azure DevOps.
///     Enable by setting ADO_STUB_PR=true in user secrets / environment variables.
/// </summary>
public sealed partial class NoOpAdoCommentPoster(ILogger<NoOpAdoCommentPoster> logger) : IAdoCommentPoster
{
    /// <inheritdoc />
    public Task<ReviewCommentPostingDiagnosticsDto> PostAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        ReviewResult result,
        Guid? clientId = null,
        IReadOnlyList<PrCommentThread>? existingThreads = null,
        CancellationToken cancellationToken = default)
    {
        LogSkippingCommentPost(logger, pullRequestId, result.Summary);
        foreach (var comment in result.Comments)
        {
            LogStubComment(logger, comment.Severity, comment.FilePath, comment.LineNumber, comment.Message);
        }

        return Task.FromResult(ReviewCommentPostingDiagnosticsDto.Empty(
            result.Comments.Count + result.CarriedForwardCandidatesSkipped,
            result.CarriedForwardCandidatesSkipped) with
        {
            PostedCount = result.Comments.Count,
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "ADO_STUB_PR is enabled — skipping comment post for PR#{PrId}. Review summary: {Summary}")]
    private static partial void LogSkippingCommentPost(ILogger logger, int prId, string summary);

    [LoggerMessage(Level = LogLevel.Information, Message = "[STUB COMMENT] {Severity} @ {File}:{Line} — {Message}")]
    private static partial void LogStubComment(ILogger logger, object severity, string? file, int? line, string message);
}
