namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Posts a reply comment into an existing pull request thread.
/// </summary>
public interface IAdoThreadReplier
{
    /// <summary>
    ///     Posts a reply comment into an existing pull request thread.
    /// </summary>
    /// <param name="organizationUrl">ADO organization URL.</param>
    /// <param name="projectId">ADO project identifier.</param>
    /// <param name="repositoryId">ADO repository identifier.</param>
    /// <param name="pullRequestId">ADO pull request number.</param>
    /// <param name="threadId">ADO thread ID to reply into.</param>
    /// <param name="replyText">Markdown text of the reply comment.</param>
    /// <param name="clientId">Optional client ID for per-client credential retrieval.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    Task ReplyAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int threadId,
        string replyText,
        Guid? clientId = null,
        CancellationToken cancellationToken = default);
}
