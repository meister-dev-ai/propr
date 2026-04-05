// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Domain.Interfaces;

/// <summary>
///     Generates a context-grounded answer to a question asked in a PR comment mention.
/// </summary>
public interface IMentionAnswerService
{
    /// <summary>
    ///     Generates an answer to a question asked in a PR comment mention,
    ///     grounded in the PR's code diff, description, and existing threads.
    /// </summary>
    /// <param name="pullRequest">The pull request providing context for the answer.</param>
    /// <param name="clientId">The client whose active AI connection should be used.</param>
    /// <param name="question">The question extracted from the mention comment.</param>
    /// <param name="threadId">The ADO thread ID the question was asked in, used to focus the AI on the relevant file and line.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The AI-generated answer text.</returns>
    Task<string> AnswerAsync(
        PullRequest pullRequest,
        Guid clientId,
        string question,
        int threadId,
        CancellationToken cancellationToken = default);
}
