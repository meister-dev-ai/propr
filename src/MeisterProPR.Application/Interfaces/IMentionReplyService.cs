using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Processes a single <see cref="MentionReplyJob" />: fetches PR context, generates an AI answer, and posts it as
///     a thread reply.
/// </summary>
public interface IMentionReplyService
{
    /// <summary>
    ///     Processes the given <see cref="MentionReplyJob" />: fetches PR context, generates an AI
    ///     answer grounded in the PR, and posts it as a reply to the original mention thread.
    /// </summary>
    /// <param name="job">The mention reply job to process.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    Task ProcessAsync(MentionReplyJob job, CancellationToken cancellationToken = default);
}
