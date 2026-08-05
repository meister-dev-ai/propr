// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

/// <summary>
///     One definition of what identifies a GitHub review thread, shared by every adapter that writes to one.
///     GitHub addresses a review thread by its GraphQL node id, which is what the thread read path carries.
/// </summary>
internal static class GitHubReviewThreadNodeId
{
    /// <summary>
    ///     Returns the thread's node id, refusing a purely numeric identifier. A number is a REST id for some
    ///     other object, a review or a comment, and can never name a thread, so it is caught here rather than
    ///     sent as an ID the GraphQL call rejects with a vaguer message.
    /// </summary>
    /// <param name="externalThreadId">The identifier the caller carries for the thread.</param>
    /// <param name="operationDescription">What the caller is about to do, named in the refusal.</param>
    /// <returns>The identifier, unchanged.</returns>
    internal static string Require(string externalThreadId, string operationDescription)
    {
        if (long.TryParse(externalThreadId, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            throw new InvalidOperationException(
                $"GitHub review thread {operationDescription} need the thread's GraphQL node id, and '{externalThreadId}' is a numeric REST identifier.");
        }

        return externalThreadId;
    }
}
